using System;
using System.Security.Cryptography;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    public class EpisodeImportFileFingerprint
    {
        public long Size { get; set; }
        public string Hash { get; set; }
    }

    public interface IEpisodeImportFileFingerprintService
    {
        EpisodeImportFileFingerprint Read(string path);
    }

    public class EpisodeImportFileFingerprintService : IEpisodeImportFileFingerprintService
    {
        private readonly IDiskProvider _disk;

        public EpisodeImportFileFingerprintService(IDiskProvider disk)
        {
            _disk = disk;
        }

        public EpisodeImportFileFingerprint Read(string path)
        {
            var size = _disk.GetFileSize(path);
            if (size <= 0)
            {
                throw new InvalidOperationException("An empty source cannot be imported.");
            }

            string hash;
            using (var stream = _disk.OpenReadStream(path))
            using (var sha256 = SHA256.Create())
            {
                hash = Convert.ToHexString(sha256.ComputeHash(stream));
            }

            if (_disk.GetFileSize(path) != size)
            {
                throw new InvalidOperationException("The file changed while its fingerprint was calculated.");
            }

            return new EpisodeImportFileFingerprint { Size = size, Hash = hash };
        }
    }
}
