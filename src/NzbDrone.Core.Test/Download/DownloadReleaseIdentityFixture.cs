using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadReleaseIdentityFixture
    {
        [Test]
        public void release_and_client_item_share_normalized_title_size_identity_without_exposing_url()
        {
            var release = new ReleaseInfo { Title = " Test Episode ", Size = 1024, DownloadUrl = "https://example.test/secret", Guid = "guid", IndexerId = 12 };
            var item = new DownloadClientItem { Title = "test episode", TotalSize = 1024 };
            var keys = DownloadReleaseIdentity.GetKeys(release);

            keys.Intersect(DownloadReleaseIdentity.GetClientKeys(item)).Should().ContainSingle();
            keys.Should().NotContain(key => key.Contains("secret") || key.Contains("example.test"));
        }

        [Test]
        public void torrent_hash_matches_case_insensitively()
        {
            var hash = new string('a', 40);
            var release = new TorrentInfo { InfoHash = hash };
            var item = new DownloadClientItem { DownloadId = hash.ToUpperInvariant(), DownloadClientInfo = new DownloadClientItemClientInfo { Protocol = DownloadProtocol.Torrent } };

            DownloadReleaseIdentity.GetKeys(release).Should().IntersectWith(DownloadReleaseIdentity.GetClientKeys(item));
        }
    }
}
