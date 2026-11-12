using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionDownloadNotificationService
    {
        void Deliver(int seriesId, int reservationId);
    }

    public class EpisodeVersionDownloadNotificationService : IEpisodeVersionDownloadNotificationService
    {
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IEpisodeVersionNotificationDeliveryService _deliveries;
        private readonly INotificationFactory _notifications;
        private readonly ISeriesService _series;
        private readonly IEpisodeService _episodes;
        private readonly IEpisodeVersionDecisionContext _contexts;
        private readonly IMediaFileService _files;
        private readonly IDownloadClientFactory _clients;
        private readonly IHistoryService _history;

        public EpisodeVersionDownloadNotificationService(IEpisodeDownloadReservationService reservations, IEpisodeVersionImportIntentService intents, IEpisodeVersionNotificationDeliveryService deliveries, INotificationFactory notifications, ISeriesService series, IEpisodeService episodes, IEpisodeVersionDecisionContext contexts, IMediaFileService files, IDownloadClientFactory clients, IHistoryService history)
        {
            _reservations = reservations;
            _intents = intents;
            _deliveries = deliveries;
            _notifications = notifications;
            _series = series;
            _episodes = episodes;
            _contexts = contexts;
            _files = files;
            _clients = clients;
            _history = history;
        }

        public void Deliver(int seriesId, int reservationId)
        {
            var reservation = _reservations.ForSeries(seriesId).SingleOrDefault(r => r.Id == reservationId);
            if (reservation == null || reservation.State != EpisodeDownloadReservationState.Imported)
            {
                throw new InvalidOperationException("Only a completed version download can notify recipients.");
            }

            var series = _series.GetSeries(seriesId);
            var available = _notifications.OnImportCompleteEnabled(false)
                .Where(n => n.Definition.Tags.Count == 0 || n.Definition.Tags.Intersect(series.Tags).Any()).ToDictionary(n => n.Definition.Id);
            _deliveries.Capture(EpisodeVersionCompletionKind.Download, seriesId, reservationId, available.Keys.ToArray());
            var pending = _deliveries.GetPending(EpisodeVersionCompletionKind.Download, seriesId, reservationId);
            if (pending.Count == 0)
            {
                return;
            }

            var ids = _reservations.GetEpisodeIds(reservationId).ToArray();
            var context = _contexts.ForVersion(new RemoteEpisode { Series = series, Episodes = _episodes.GetEpisodes(ids) }, reservation.SeriesVersionId);
            var imports = _intents.GetForSeries(seriesId).Where(i => i.ReservationId == reservationId && i.State == EpisodeVersionImportIntentState.Completed).ToList();
            var fileIds = imports.Where(i => i.RegisteredFileId.HasValue).Select(i => i.RegisteredFileId.Value).Distinct().ToArray();
            var files = _files.GetFiles(fileIds);
            if (ids.Length == 0 || context.Episodes.Count != ids.Length || files.Count != fileIds.Length ||
                !context.Episodes.Select(e => e.Id).ToHashSet().SetEquals(ids) ||
                !context.Episodes.Select(e => e.EpisodeFileId).ToHashSet().SetEquals(fileIds) || files.Any(f => f.SeriesId != seriesId))
            {
                throw new InvalidOperationException("The completed download's episode file ownership changed.");
            }
            foreach (var file in files)
            {
                file.Series = context.Series;
                file.Episodes = context.Episodes.Where(e => e.EpisodeFileId == file.Id).ToList();
                file.Path = Path.Combine(series.Path, file.RelativePath);
            }
            var client = reservation.DownloadClientId.HasValue ? _clients.Find(reservation.DownloadClientId.Value) : null;
            if (client == null || string.IsNullOrWhiteSpace(reservation.DownloadId))
            {
                throw new InvalidOperationException("The completed download's client identity is unavailable.");
            }

            var grabs = _history.FindByDownloadId(reservation.DownloadId).Where(h => h.EventType == EpisodeHistoryEventType.Grabbed && h.SeriesVersionId == reservation.SeriesVersionId && h.Data.GetValueOrDefault(EpisodeHistory.DOWNLOAD_CLIENT_ID) == client.Id.ToString()).OrderByDescending(h => h.Date).ToList();
            if (grabs.Count == 0 || grabs[0].Quality == null)
            {
                throw new InvalidOperationException("The completed download's original release history is unavailable.");
            }

            foreach (var recipient in pending)
            {
                _deliveries.Deliver(EpisodeVersionCompletionKind.Download, seriesId, reservationId, recipient.NotificationId, deliveryId =>
                {
                    if (!available.TryGetValue(recipient.NotificationId, out var provider))
                    {
                        throw new InvalidOperationException("The captured notification recipient is no longer available.");
                    }
                    provider.OnImportComplete(new ImportCompleteMessage
                    {
                        Message = series.Title + " - " + context.SeriesVersionLabel + " - Import complete",
                        Series = context.Series,
                        Episodes = context.Episodes,
                        EpisodeFiles = files,
                        SourcePath = imports.Select(i => Path.GetDirectoryName(i.SourcePath)).ToList().GetLongestCommonPath(),
                        DestinationPath = files.Select(f => Path.GetDirectoryName(f.Path)).ToList().GetLongestCommonPath(),
                        DownloadId = reservation.DownloadId,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = client.Id, Name = client.Name, Type = client.Implementation, Protocol = client.Protocol },
                        VersionReservationId = reservationId,
                        NotificationDeliveryId = deliveryId,
                        ReleaseQuality = grabs[0].Quality,
                        ReleaseGroup = grabs[0].Data.GetValueOrDefault("releaseGroup"),
                        Release = new GrabbedReleaseInfo(grabs),
                        SourceTitle = grabs[0].SourceTitle
                    });
                });
            }
        }
    }
}
