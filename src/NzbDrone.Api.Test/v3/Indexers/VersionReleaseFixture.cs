using System.Net;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;
using Sonarr.Api.V3.Indexers;

namespace NzbDrone.Api.Test.v3.Indexers
{
    [TestFixture]
    public class VersionReleaseFixture
    {
        [Test]
        public void mapped_release_preserves_version_identity()
        {
            var remote = new RemoteEpisode { SeriesVersionId = 7, Release = new ReleaseInfo(), ParsedEpisodeInfo = new ParsedEpisodeInfo() };
            var resource = new DownloadDecision(remote).ToResource();
            resource.SeriesVersionId.Should().Be(7);
        }

        [Test]
        public void manual_override_cannot_strip_a_cached_version_identity()
        {
            var cache = new CacheManager();
            cache.GetCache<RemoteEpisode>(typeof(ReleaseController), "remoteEpisodes")
                .Set("1_4_guid_7", new RemoteEpisode { SeriesVersionId = 7 });
            var download = new Mock<IDownloadService>();
            var controller = new ReleaseController(Mock.Of<IFetchAndParseRss>(), Mock.Of<ISearchForReleases>(), Mock.Of<IMakeDownloadDecision>(),
                Mock.Of<IPrioritizeDownloadDecision>(), download.Object, Mock.Of<ISeriesService>(), Mock.Of<IEpisodeService>(),
                Mock.Of<IParsingService>(), cache, Mock.Of<IQualityProfileService>(), LogManager.GetCurrentClassLogger());

            var error = Assert.ThrowsAsync<NzbDroneClientException>(async () => await controller.DownloadRelease(new ReleaseResource { IndexerId = 1, Guid = "guid", SeriesVersionId = 7, ShouldOverride = true }));
            error.StatusCode.Should().Be(HttpStatusCode.Conflict);
            download.Verify(d => d.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }
    }
}
