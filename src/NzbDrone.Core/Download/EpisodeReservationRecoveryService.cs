using System;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.Download
{
    public interface IEpisodeReservationRecoveryService
    {
        void BindTrackedDownload(int seriesId, int reservationId, int clientId, string downloadId, string note);
    }

    // Explicit reconciliation only. It does not infer that a missing job was
    // never submitted, and is not yet connected to an API or automatic dispatch.
    public class EpisodeReservationRecoveryService : IEpisodeReservationRecoveryService
    {
        private readonly IMainDatabase _database;
        private readonly ITrackedDownloadService _trackedDownloads;

        public EpisodeReservationRecoveryService(IMainDatabase database, ITrackedDownloadService trackedDownloads)
        {
            _database = database;
            _trackedDownloads = trackedDownloads;
        }

        public void BindTrackedDownload(int seriesId, int reservationId, int clientId, string downloadId, string note)
        {
            if (seriesId <= 0 || reservationId <= 0 || clientId <= 0 || string.IsNullOrWhiteSpace(downloadId) || downloadId.Length > 255 || downloadId != downloadId.Trim() || string.IsNullOrWhiteSpace(note) || note.Length > 255)
            {
                throw new ArgumentException("The series, reservation, client, download ID and a short explanation are required.");
            }

            lock (EpisodeDownloadSynchronization.For(clientId, downloadId))
            {
                BindTrackedDownloadCore(seriesId, reservationId, clientId, downloadId, note);
            }
        }

        private void BindTrackedDownloadCore(int seriesId, int reservationId, int clientId, string downloadId, string note)
        {

            var tracked = _trackedDownloads.GetTrackedDownloads()
                .Where(t => t?.DownloadItem != null && t.DownloadClient == clientId && string.Equals(t.DownloadItem.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tracked.Count != 1 || !tracked[0].IsTrackable || tracked[0].State is TrackedDownloadState.Importing or TrackedDownloadState.Imported or TrackedDownloadState.Failed or TrackedDownloadState.FailedPending or TrackedDownloadState.Ignored ||
                tracked[0].RemoteEpisode?.Series?.Id != seriesId || tracked[0].RemoteEpisode.Episodes == null || tracked[0].RemoteEpisode.Episodes.Count == 0)
            {
                throw new InvalidOperationException("The tracked job is unavailable, ambiguous, or does not identify the requested series.");
            }

            var item = tracked[0];
            var canonicalDownloadId = item.DownloadItem.DownloadId;
            if (item.RemoteEpisode.Episodes.Any(e => e == null || e.SeriesId != seriesId))
            {
                throw new InvalidOperationException("The tracked job contains an invalid episode or a different series.");
            }

            var episodes = item.RemoteEpisode.Episodes.Select(e => e.Id).ToArray();
            var releaseKeys = DownloadReleaseIdentity.GetClientKeys(item.DownloadItem);
            if (canonicalDownloadId.Length > 255 || canonicalDownloadId != canonicalDownloadId.Trim() || episodes.Any(id => id <= 0) || episodes.Distinct().Count() != episodes.Length || releaseKeys.Count == 0)
            {
                throw new InvalidOperationException("The tracked job does not have a complete episode and release identity.");
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            if (connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The series no longer exists.");
            }

            var candidates = connection.Query<EpisodeDownloadReservation>(@"SELECT DISTINCT r.* FROM ""EpisodeDownloadReservations"" r
                JOIN ""EpisodeDownloadReservationKeys"" k ON k.""ReservationId"" = r.""Id""
                WHERE r.""DownloadClientId"" = @clientId AND r.""DownloadId"" IS NULL
                  AND r.""State"" = @state AND k.""ReleaseKey"" IN @releaseKeys", new { clientId, state = (int)EpisodeDownloadReservationState.Uncertain, releaseKeys = releaseKeys.ToArray() }, transaction).ToList();
            if (candidates.Count != 1 || candidates[0].Id != reservationId || candidates[0].SeriesId != seriesId ||
                (item.SeriesVersionId.HasValue && item.SeriesVersionId != candidates[0].SeriesVersionId) ||
                (item.EpisodeDownloadReservationId.HasValue && item.EpisodeDownloadReservationId != reservationId))
            {
                throw new InvalidOperationException("The tracked release does not uniquely match the reservation or has conflicting ownership.");
            }

            var reservedEpisodes = connection.Query<int>("SELECT \"EpisodeId\" FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId AND \"SeriesVersionId\" = @seriesVersionId", new { reservationId, seriesVersionId = candidates[0].SeriesVersionId }, transaction).ToHashSet();
            if (!reservedEpisodes.SetEquals(episodes))
            {
                throw new InvalidOperationException("The tracked job does not cover exactly the reserved episodes.");
            }

            var normalizedDownloadId = canonicalDownloadId.ToUpperInvariant();
            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservations\" WHERE \"DownloadClientId\" = @clientId AND \"NormalizedDownloadId\" = @normalizedDownloadId", new { clientId, normalizedDownloadId }, transaction) != 0)
            {
                throw new InvalidOperationException("The client job is already bound to another reservation.");
            }

            var updated = connection.Execute("UPDATE \"EpisodeDownloadReservations\" SET \"State\" = @submitted, \"DownloadId\" = @canonicalDownloadId, \"NormalizedDownloadId\" = @normalizedDownloadId, \"UpdatedAt\" = @updatedAt, \"Note\" = @note WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId AND \"DownloadClientId\" = @clientId AND \"State\" = @uncertain AND \"DownloadId\" IS NULL", new { submitted = (int)EpisodeDownloadReservationState.Submitted, canonicalDownloadId, normalizedDownloadId, updatedAt = DateTime.UtcNow, note, reservationId, seriesId, clientId, uncertain = (int)EpisodeDownloadReservationState.Uncertain }, transaction);
            if (updated != 1)
            {
                throw new InvalidOperationException("The reservation changed while it was being reconciled.");
            }

            transaction.Commit();
            item.EpisodeDownloadReservationId = reservationId;
            item.SeriesVersionId = candidates[0].SeriesVersionId;
            item.HasUnresolvedReservation = false;
            item.State = TrackedDownloadState.ImportBlocked;
        }
    }
}
