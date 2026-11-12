namespace NzbDrone.Core.MediaFiles
{
    internal static class EpisodeVersionImportSynchronization
    {
        private static readonly object[] Locks = CreateLocks();

        public static object For(int importId) => Locks[(int)((uint)importId % (uint)Locks.Length)];

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
