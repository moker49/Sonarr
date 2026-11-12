using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class SeriesOperationSynchronizationFixture
    {
        [Test]
        public async Task synchronous_and_asynchronous_operations_share_the_same_series_lease()
        {
            var first = SeriesOperationSynchronization.Acquire(42);
            var waiting = SeriesOperationSynchronization.AcquireAsync(42);
            try
            {
                waiting.IsCompleted.Should().BeFalse();
            }
            finally
            {
                first.Dispose();
            }
            using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task duplicate_stripes_are_acquired_once_and_disposal_is_idempotent()
        {
            var first = SeriesOperationSynchronization.AcquireMany(new[] { 3, 259, 3 });
            first.Dispose();
            first.Dispose();
            using var second = await SeriesOperationSynchronization.AcquireAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
            using var other = await SeriesOperationSynchronization.AcquireAsync(4).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
