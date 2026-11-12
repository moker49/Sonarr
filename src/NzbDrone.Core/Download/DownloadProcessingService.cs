using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly Logger _logger;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         IEpisodeDownloadReservationService reservations,
                                         Logger logger)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _reservations = reservations;
            _logger = logger;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported && !t.EpisodeDownloadReservationId.HasValue && !t.SeriesVersionId.HasValue && !t.HasUnresolvedReservation)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                lock (EpisodeDownloadSynchronization.For(trackedDownload.DownloadClient, trackedDownload.DownloadItem?.DownloadId))
                {
                    if (!trackedDownload.DownloadItem.Removed && trackedDownload.DownloadItem.CanBeRemoved && trackedDownload.State == TrackedDownloadState.Imported && !trackedDownload.EpisodeDownloadReservationId.HasValue && !trackedDownload.SeriesVersionId.HasValue && !trackedDownload.HasUnresolvedReservation && !_reservations.HasUnboundForClient(trackedDownload.DownloadClient))
                    {
                        _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
                    }
                }
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => t.IsTrackable)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    lock (EpisodeDownloadSynchronization.For(trackedDownload.DownloadClient, trackedDownload.DownloadItem?.DownloadId))
                    {
                        ProcessTrackedDownload(trackedDownload, enableCompletedDownloadHandling);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }

        private void ProcessTrackedDownload(TrackedDownload trackedDownload, bool enableCompletedDownloadHandling)
        {
            if (trackedDownload.EpisodeDownloadReservationId.HasValue || trackedDownload.SeriesVersionId.HasValue || trackedDownload.HasUnresolvedReservation || _reservations.HasUnboundForClient(trackedDownload.DownloadClient))
            {
                if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending)
                {
                    // The completed service validates the bound reservation and
                    // uses the journaled version import path.
                    _completedDownloadService.Import(trackedDownload);
                    return;
                }
                if (trackedDownload.State == TrackedDownloadState.Imported)
                {
                    return;
                }
                trackedDownload.Warn("Version-aware download processing is not yet available for this download.");
                trackedDownload.State = TrackedDownloadState.ImportBlocked;
                return;
            }

            // Process completed items followed by failed, this allows failed imports to have
            // their state changed and be processed immediately instead of the next execution.
            if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending)
            {
                _completedDownloadService.Import(trackedDownload);
            }

            if (trackedDownload.State == TrackedDownloadState.FailedPending)
            {
                _failedDownloadService.ProcessFailed(trackedDownload);
            }
        }
    }
}
