using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionImportNotificationFixture : CoreTest<EpisodeVersionImportNotificationService>
    {
        [Test]
        public void delivers_version_context_and_stable_delivery_identity_to_the_captured_provider()
        {
            var provider = new Mock<INotification>();
            provider.SetupGet(n => n.Definition).Returns(new NotificationDefinition { Id = 7, Tags = new HashSet<int>() });
            var series = new Series { Id = 1, Title = "Show", Tags = new HashSet<int>() };
            var episode = new Episode { Id = 5, SeriesId = 1, EpisodeFileId = 4 };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeVersionImportIntent> { new EpisodeVersionImportIntent { Id = 2, SeriesId = 1, SeriesVersionId = 3, ReservationId = 8, RegisteredFileId = 4, State = EpisodeVersionImportIntentState.Completed, SourcePath = "source", DestinationPath = "destination" } });
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 5 });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(new List<Episode> { episode });
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Setup(s => s.ForVersion(It.IsAny<RemoteEpisode>(), 3)).Returns(new RemoteEpisode { Series = series, Episodes = new List<Episode> { episode }, SeriesVersionId = 3, SeriesVersionLabel = "HD" });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.Get(4)).Returns(new EpisodeFile { Id = 4, SeriesId = 1 });
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.ForSeries(1)).Returns(new List<EpisodeDownloadReservation> { new EpisodeDownloadReservation { Id = 8, SeriesVersionId = 3, DownloadClientId = 9, DownloadId = "job" } });
            Mocker.GetMock<IDownloadClientFactory>().Setup(s => s.Find(9)).Returns(new DownloadClientDefinition { Id = 9, Name = "Client" });
            Mocker.GetMock<INotificationFactory>().Setup(s => s.OnDownloadEnabled(false)).Returns(new List<INotification> { provider.Object });
            Mocker.GetMock<IEpisodeVersionNotificationDeliveryService>().Setup(s => s.GetPending(EpisodeVersionCompletionKind.Import, 1, 2)).Returns(new List<EpisodeVersionNotificationDelivery> { new EpisodeVersionNotificationDelivery { NotificationId = 7 } });
            Mocker.GetMock<IEpisodeVersionNotificationDeliveryService>().Setup(s => s.Deliver(EpisodeVersionCompletionKind.Import, 1, 2, 7, It.IsAny<Action<int>>())).Callback<EpisodeVersionCompletionKind, int, int, int, Action<int>>((kind, seriesId, importId, notificationId, send) => send(42)).Returns(true);

            Subject.Deliver(1, 2);

            provider.Verify(n => n.OnDownload(It.Is<DownloadMessage>(m => m.VersionImportId == 2 && m.NotificationDeliveryId == 42 && m.EpisodeInfo.SeriesVersionId == 3 && m.EpisodeInfo.SeriesVersionLabel == "HD" && m.EpisodeFile.Episodes.Value[0].Id == 5 && m.DownloadId == "job")), Times.Once());
        }
    }
}
