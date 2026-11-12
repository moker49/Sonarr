using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Tv
{
    // Shared by root operations, not nested helpers. Bounded stripes prevent a
    // long-running server from retaining one semaphore per historical series.
    public static class SeriesOperationSynchronization
    {
        private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        public static IDisposable Acquire(int seriesId) => AcquireMany(new[] { seriesId });

        public static IDisposable AcquireMany(IEnumerable<int> seriesIds)
        {
            var locks = seriesIds.Select(id => (int)((uint)id % (uint)Locks.Length)).Distinct().OrderBy(id => id).Select(id => Locks[id]).ToArray();
            var held = new List<SemaphoreSlim>();
            try
            {
                foreach (var semaphore in locks)
                {
                    semaphore.Wait();
                    held.Add(semaphore);
                }
                return new Lease(held.ToArray());
            }
            catch
            {
                foreach (var semaphore in held)
                {
                    semaphore.Release();
                }
                throw;
            }
        }

        public static async Task<IDisposable> AcquireAsync(int seriesId)
        {
            var semaphore = Locks[(int)((uint)seriesId % (uint)Locks.Length)];
            await semaphore.WaitAsync().ConfigureAwait(false);
            return new Lease(new[] { semaphore });
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim[] _held;
            public Lease(SemaphoreSlim[] held) => _held = held;

            public void Dispose()
            {
                var held = Interlocked.Exchange(ref _held, null);
                if (held != null)
                {
                    foreach (var semaphore in held.Reverse())
                    {
                        semaphore.Release();
                    }
                }
            }
        }
    }
}
