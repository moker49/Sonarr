using System;

namespace NzbDrone.Core.Download
{
    // Bounded stripes avoid retaining a lock for every download ever seen.
    public static class EpisodeDownloadSynchronization
    {
        private static readonly object[] Locks = CreateLocks();

        public static object For(int clientId, string downloadId)
        {
            // The legacy tracker cache is keyed by download ID, across clients.
            // Equal IDs must therefore share a lock even when clients differ.
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(downloadId ?? string.Empty);
            return Locks[(int)((uint)hash % (uint)Locks.Length)];
        }

        private static object[] CreateLocks()
        {
            var locks = new object[256];
            for (var i = 0; i < locks.Length; i++)
            {
                locks[i] = new object();
            }

            return locks;
        }
    }
}
