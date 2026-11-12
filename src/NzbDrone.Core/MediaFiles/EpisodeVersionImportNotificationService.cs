using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionImportNotificationService
    {
        void Deliver(int seriesId, int importId);
    }

    public class EpisodeVersionImportNotificationService : IEpisodeVersionImportNotificationService
    {
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IEpisodeVersionNotificationDeliveryService _deliveries;
        private readonly INotificationFactory _notifications;
        private readonly ISeriesService _series;
        private readonly IEpisodeService _episodes;
        private readonly IEpisodeVersionDecisionContext _contexts;
        private readonly IMediaFileService _files;
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly IDownloadClientFactory _clients;

        public EpisodeVersionImportNotificationService(IEpisodeVersionImportIntentService intents, IEpisodeVersionNotificationDeliveryService deliveries, INotificationFactory notifications, ISeriesService series, IEpisodeService episodes, IEpisodeVersionDecisionContext contexts, IMediaFileService files, IEpisodeDownloadReservationService reservations, IDownloadClientFactory clients)
        {
            _intents = intents;
            _deliveries = deliveries;
            _notifications = notifications;
            _series = series;
            _episodes = episodes;
            _contexts = contexts;
            _files = files;
            _reservations = reservations;
            _clients = clients;
        }

        public void Deliver(int seriesId, int importId)
        {
            var intent = _intents.GetForSeries(seriesId).SingleOrDefault(i => i.Id == importId);
            if (intent == null || intent.State != EpisodeVersionImportIntentState.Completed || !intent.RegisteredFileId.HasValue)
            {
                throw new InvalidOperationException("Only a completed version import can notify recipients.");
            }

            var series = _series.GetSeries(seriesId);
            var available = _notifications.OnDownloadEnabled(false)
                .Where(n => n.Definition.Tags.Count == 0 || n.Definition.Tags.Intersect(series.Tags).Any()).ToDictionary(n => n.Definition.Id);
            _deliveries.Capture(EpisodeVersionCompletionKind.Import, seriesId, importId, available.Keys.ToArray());
            var pending = _deliveries.GetPending(EpisodeVersionCompletionKind.Import, seriesId, importId);
            if (pending.Count == 0)
            {
                return;
            }

            var context = _contexts.ForVersion(new RemoteEpisode { Series = series, Episodes = _episodes.GetEpisodes(_intents.GetEpisodeIds(importId)) }, intent.SeriesVersionId);
            var file = _files.Get(intent.RegisteredFileId.Value);
            if (file.SeriesId != seriesId || context.Episodes.Count == 0 || context.Episodes.Any(e => e.EpisodeFileId != file.Id))
            {
                throw new InvalidOperationException("The notification's version file ownership changed.");
            }
            var reservation = _reservations.ForSeries(seriesId).Single(r => r.Id == intent.ReservationId);
            if (reservation.SeriesVersionId != intent.SeriesVersionId || string.IsNullOrWhiteSpace(reservation.DownloadId))
            {
                throw new InvalidOperationException("The notification's reservation identity changed.");
            }
            var client = reservation.DownloadClientId.HasValue ? _clients.Find(reservation.DownloadClientId.Value) : null;
            if (client == null)
            {
                throw new InvalidOperationException("The notification's download client is unavailable.");
            }

            file.Series = context.Series;
            file.Episodes = context.Episodes;
            file.Path = intent.DestinationPath;

            foreach (var recipient in pending)
            {
                _deliveries.Deliver(EpisodeVersionCompletionKind.Import, seriesId, importId, recipient.NotificationId, deliveryId =>
                {
                    if (!available.TryGetValue(recipient.NotificationId, out var provider))
                    {
                        throw new InvalidOperationException("The captured notification recipient is no longer available.");
                    }
                    provider.OnDownload(new DownloadMessage
                    {
                        Message = series.Title + " - " + string.Join(", ", context.Episodes.Select(e => $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}")) + " - " + context.SeriesVersionLabel,
                        Series = context.Series,
                        EpisodeFile = file,
                        EpisodeInfo = new LocalEpisode { Series = context.Series, SeriesVersionId = intent.SeriesVersionId, SeriesVersionLabel = context.SeriesVersionLabel, Episodes = context.Episodes, Path = intent.SourcePath, Quality = file.Quality, Languages = file.Languages },
                        OldFiles = new List<DeletedEpisodeFile>(),
                        SourcePath = intent.SourcePath,
                        DownloadId = reservation.DownloadId,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = client.Id, Name = client.Name, Type = client.Implementation, Protocol = client.Protocol },
                        NotificationDeliveryId = deliveryId,
                        VersionImportId = importId
                    });
                });
            }
        }
    }
}
