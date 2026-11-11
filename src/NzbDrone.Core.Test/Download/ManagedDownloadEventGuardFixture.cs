using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class ManagedDownloadEventGuardFixture : CoreTest<DownloadEventHub>
    {
        [Test]
        public void managed_jobs_do_not_use_legacy_client_cleanup_events()
        {
            var tracked = new TrackedDownload
            {
                DownloadClient = 1,
                EpisodeDownloadReservationId = 2,
                SeriesVersionId = 3,
                DownloadItem = new DownloadClientItem { DownloadId = "job", CanBeRemoved = true }
            };

            Subject.Handle(new DownloadFailedEvent { TrackedDownload = tracked });
            Subject.Handle(new DownloadCompletedEvent(tracked, 1, null, null));
            Subject.Handle(new DownloadCanBeRemovedEvent(tracked));

            Mocker.GetMock<IProvideDownloadClient>().Verify(p => p.Get(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void unresolved_client_dispatch_blocks_cleanup_for_stale_tracked_job()
        {
            var tracked = new TrackedDownload
            {
                DownloadClient = 7,
                DownloadItem = new DownloadClientItem { DownloadId = "job", CanBeRemoved = true }
            };
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.HasUnboundForClient(7)).Returns(true);

            Subject.Handle(new DownloadCanBeRemovedEvent(tracked));

            Mocker.GetMock<IProvideDownloadClient>().Verify(p => p.Get(It.IsAny<int>()), Times.Never());
        }
    }
}
