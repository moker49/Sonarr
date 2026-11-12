using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class ManagedDownloadProcessingGuardFixture : CoreTest<DownloadProcessingService>
    {
        [Test]
        public void managed_import_pending_job_is_delegated_to_validating_completed_service()
        {
            var tracked = new TrackedDownload { IsTrackable = true, State = TrackedDownloadState.ImportPending, EpisodeDownloadReservationId = 1, SeriesVersionId = 2, DownloadItem = new DownloadClientItem { DownloadId = "job" } };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { tracked });
            Mocker.GetMock<NzbDrone.Core.Configuration.IConfigService>().SetupGet(s => s.EnableCompletedDownloadHandling).Returns(true);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.Import(tracked), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.ProcessFailed(It.IsAny<TrackedDownload>()), Times.Never());
        }

        [Test]
        public void managed_failed_or_imported_jobs_are_not_processed_or_removed_by_legacy_pipeline()
        {
            var failed = new TrackedDownload
            {
                IsTrackable = true,
                State = TrackedDownloadState.FailedPending,
                EpisodeDownloadReservationId = 1,
                SeriesVersionId = 2,
                DownloadItem = new DownloadClientItem { DownloadId = "failed", Title = "Failed" }
            };
            var imported = new TrackedDownload
            {
                IsTrackable = false,
                State = TrackedDownloadState.Imported,
                EpisodeDownloadReservationId = 3,
                SeriesVersionId = 4,
                DownloadItem = new DownloadClientItem { DownloadId = "imported", Title = "Imported", CanBeRemoved = true }
            };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { failed, imported });

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            failed.State.Should().Be(TrackedDownloadState.ImportBlocked);
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.ProcessFailed(It.IsAny<TrackedDownload>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<DownloadCanBeRemovedEvent>()), Times.Never());
        }

        [Test]
        public void unresolved_client_dispatch_blocks_stale_cached_job_from_failed_processing()
        {
            var tracked = new TrackedDownload
            {
                IsTrackable = true,
                DownloadClient = 7,
                State = TrackedDownloadState.FailedPending,
                DownloadItem = new DownloadClientItem { DownloadId = "job", Title = "Job" }
            };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { tracked });
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.HasUnboundForClient(7)).Returns(true);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.ProcessFailed(It.IsAny<TrackedDownload>()), Times.Never());
        }
    }
}
