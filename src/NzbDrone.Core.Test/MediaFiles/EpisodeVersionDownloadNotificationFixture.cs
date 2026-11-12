using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionDownloadNotificationFixture : CoreTest<EpisodeVersionDownloadNotificationService>
    {
        private Episode _episode;
        private Mock<INotification> _provider;

        [SetUp]
        public void Setup()
        {
            var series = new Series { Id = 1, Title = "Show", Path = @"C:\TV\Show".AsOsAgnostic(), Tags = new HashSet<int>() };
            _episode = new Episode { Id = 5, SeriesId = 1, EpisodeFileId = 4 };
            _provider = new Mock<INotification>();
            _provider.SetupGet(p => p.Definition).Returns(new NotificationDefinition { Id = 7, Tags = new HashSet<int>() });
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.ForSeries(1)).Returns(new List<EpisodeDownloadReservation> { new EpisodeDownloadReservation { Id = 2, SeriesId = 1, SeriesVersionId = 3, State = EpisodeDownloadReservationState.Imported, DownloadClientId = 9, DownloadId = "job" } });
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 5 });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(new List<Episode> { _episode });
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Setup(s => s.ForVersion(It.IsAny<RemoteEpisode>(), 3)).Returns(new RemoteEpisode { Series = series, Episodes = new List<Episode> { _episode }, SeriesVersionLabel = "HD" });
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeVersionImportIntent> { new EpisodeVersionImportIntent { Id = 10, ReservationId = 2, State = EpisodeVersionImportIntentState.Completed, RegisteredFileId = 4, SourcePath = @"C:\Downloads\Show\file.mkv".AsOsAgnostic() } });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFiles(It.IsAny<IEnumerable<int>>())).Returns(new List<EpisodeFile> { new EpisodeFile { Id = 4, SeriesId = 1, RelativePath = "Show - HD.mkv" } });
            Mocker.GetMock<IDownloadClientFactory>().Setup(s => s.Find(9)).Returns(new DownloadClientDefinition { Id = 9, Name = "Client" });
            var grab = new EpisodeHistory { SeriesVersionId = 3, EpisodeId = 5, SourceTitle = "Original release", Quality = new QualityModel(Quality.HDTV720p), EventType = EpisodeHistoryEventType.Grabbed };
            grab.Data[EpisodeHistory.DOWNLOAD_CLIENT_ID] = "9";
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId("job")).Returns(new List<EpisodeHistory> { grab });
            Mocker.GetMock<INotificationFactory>().Setup(s => s.OnImportCompleteEnabled(false)).Returns(new List<INotification> { _provider.Object });
            Mocker.GetMock<IEpisodeVersionNotificationDeliveryService>().Setup(s => s.GetPending(EpisodeVersionCompletionKind.Download, 1, 2)).Returns(new List<EpisodeVersionNotificationDelivery> { new EpisodeVersionNotificationDelivery { NotificationId = 7 } });
            Mocker.GetMock<IEpisodeVersionNotificationDeliveryService>().Setup(s => s.Deliver(EpisodeVersionCompletionKind.Download, 1, 2, 7, It.IsAny<Action<int>>())).Callback<EpisodeVersionCompletionKind, int, int, int, Action<int>>((kind, seriesId, owner, provider, send) => send(42)).Returns(true);
        }

        [Test]
        public void delivers_original_release_and_version_files_with_stable_identity()
        {
            Subject.Deliver(1, 2);
            _provider.Verify(p => p.OnImportComplete(It.Is<ImportCompleteMessage>(m => m.VersionReservationId == 2 && m.NotificationDeliveryId == 42 && m.ReleaseQuality.Quality == Quality.HDTV720p && m.Release.Title == "Original release" && m.EpisodeFiles[0].Episodes.Value[0].Id == 5)), Times.Once());
        }

        [Test]
        public void changed_ownership_prevents_delivery()
        {
            _episode.EpisodeFileId = 99;
            Assert.Throws<InvalidOperationException>(() => Subject.Deliver(1, 2));
            _provider.Verify(p => p.OnImportComplete(It.IsAny<ImportCompleteMessage>()), Times.Never());
        }
    }
}
