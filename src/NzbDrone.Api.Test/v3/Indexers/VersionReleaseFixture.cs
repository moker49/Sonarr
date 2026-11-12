using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private ReleaseController ControllerFor(RemoteEpisode remote, Mock<IDownloadService> download)
        {
            var cache = new CacheManager();
            cache.GetCache<RemoteEpisode>(typeof(ReleaseController), "remoteEpisodes").Set("1_4_guid_7", remote);
            return new ReleaseController(Mock.Of<IFetchAndParseRss>(), Mock.Of<ISearchForReleases>(), Mock.Of<IMakeDownloadDecision>(),
                Mock.Of<IPrioritizeDownloadDecision>(), download.Object, Mock.Of<ISeriesService>(), Mock.Of<IEpisodeService>(),
                Mock.Of<IParsingService>(), cache, Mock.Of<IQualityProfileService>(), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public async Task matching_targeted_grab_reaches_validating_download_service()
        {
            var remote = new RemoteEpisode { SeriesVersionId = 7, Series = new NzbDrone.Core.Tv.Series { Id = 2 }, Episodes = new List<Episode> { new Episode { Id = 5 } } };
            var download = new Mock<IDownloadService>();
            download.Setup(s => s.DownloadReport(remote, null)).Returns(Task.CompletedTask);
            var controller = ControllerFor(remote, download);

            await controller.DownloadRelease(new ReleaseResource { IndexerId = 1, Guid = "guid", SeriesVersionId = 7, SeriesId = 2, EpisodeIds = new List<int> { 5 } });

            download.Verify(s => s.DownloadReport(remote, null), Times.Once());
        }

        [Test]
        public void final_dispatch_revalidation_failure_is_reported_as_a_conflict()
        {
            var remote = new RemoteEpisode { SeriesVersionId = 7, Series = new NzbDrone.Core.Tv.Series { Id = 2 }, Episodes = new List<Episode> { new Episode { Id = 5 } } };
            var download = new Mock<IDownloadService>();
            download.Setup(s => s.DownloadReport(remote, null)).ThrowsAsync(new System.InvalidOperationException("Version slot became occupied"));
            var controller = ControllerFor(remote, download);

            var error = Assert.ThrowsAsync<NzbDroneClientException>(() => controller.DownloadRelease(new ReleaseResource { IndexerId = 1, Guid = "guid", SeriesVersionId = 7 }));

            error.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void targeted_grab_cannot_redirect_series_or_episode_coverage(bool wrongSeries)
        {
            var remote = new RemoteEpisode { SeriesVersionId = 7, Series = new NzbDrone.Core.Tv.Series { Id = 2 }, Episodes = new List<Episode> { new Episode { Id = 5 } } };
            var download = new Mock<IDownloadService>();
            var controller = ControllerFor(remote, download);

            var error = Assert.ThrowsAsync<NzbDroneClientException>(() => controller.DownloadRelease(new ReleaseResource { IndexerId = 1, Guid = "guid", SeriesVersionId = 7, SeriesId = wrongSeries ? 99 : 2, EpisodeIds = wrongSeries ? new List<int> { 5 } : new List<int> { 6 } }));

            error.StatusCode.Should().Be(HttpStatusCode.Conflict);
            download.Verify(s => s.DownloadReport(It.IsAny<RemoteEpisode>(), It.IsAny<int?>()), Times.Never());
        }
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
