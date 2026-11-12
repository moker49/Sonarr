using System;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionDownloadCompletionService
    {
        bool Complete(int seriesId, int reservationId);
    }

    public class EpisodeVersionDownloadCompletionService : IEpisodeVersionDownloadCompletionService
    {
        private readonly IMainDatabase _database;

        public EpisodeVersionDownloadCompletionService(IMainDatabase database)
        {
            _database = database;
        }

        public bool Complete(int seriesId, int reservationId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            if (connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The download's series no longer exists.");
            }

            var reservation = connection.QuerySingleOrDefault<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId", new { reservationId, seriesId }, transaction);
            if (reservation == null)
            {
                throw new InvalidOperationException("The download reservation does not belong to this series.");
            }

            if (reservation.State == EpisodeDownloadReservationState.Imported)
            {
                return true;
            }

            if (reservation.State != EpisodeDownloadReservationState.Submitted || !reservation.DownloadClientId.HasValue || string.IsNullOrWhiteSpace(reservation.DownloadId))
            {
                throw new InvalidOperationException("Only a submitted download can be completed.");
            }

            var ids = connection.Query<int>("SELECT \"EpisodeId\" FROM \"EpisodeDownloadReservationEpisodes\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction).ToArray();
            if (ids.Length == 0)
            {
                throw new InvalidOperationException("The reservation has no durable episode coverage.");
            }

            var covered = connection.Query<int>(@"SELECT DISTINCT ie.""EpisodeId"" FROM ""EpisodeVersionImportIntents"" i
                JOIN ""EpisodeVersionImportEpisodes"" ie ON ie.""ImportId"" = i.""Id""
                JOIN ""EpisodeVersionAssignments"" a ON a.""EpisodeId"" = ie.""EpisodeId"" AND a.""SeriesVersionId"" = i.""SeriesVersionId""
                JOIN ""VersionEpisodeFiles"" vf ON vf.""Id"" = a.""VersionEpisodeFileId"" AND vf.""SeriesVersionId"" = i.""SeriesVersionId"" AND vf.""EpisodeFileId"" = i.""RegisteredFileId""
                JOIN ""EpisodeFiles"" f ON f.""Id"" = vf.""EpisodeFileId"" AND f.""SeriesId"" = i.""SeriesId""
                JOIN ""Episodes"" e ON e.""Id"" = ie.""EpisodeId"" AND e.""SeriesId"" = i.""SeriesId""
                WHERE i.""ReservationId"" = @reservationId AND i.""SeriesId"" = @seriesId AND i.""SeriesVersionId"" = @versionId AND i.""State"" = @completed", new { reservationId, seriesId, versionId = reservation.SeriesVersionId, completed = (int)EpisodeVersionImportIntentState.Completed }, transaction).ToHashSet();
            var unfinished = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportIntents\" WHERE \"ReservationId\" = @reservationId AND \"State\" NOT IN @terminal", new { reservationId, terminal = new[] { (int)EpisodeVersionImportIntentState.Completed, (int)EpisodeVersionImportIntentState.Cancelled } }, transaction);
            if (!covered.SetEquals(ids) || unfinished != 0)
            {
                return false;
            }

            var active = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""SeriesVersions"" v
                JOIN ""CompositeProfileVersions"" p ON p.""Id"" = v.""CompositeProfileVersionId""
                JOIN ""Series"" s ON s.""Id"" = v.""SeriesId"" AND s.""QualityProfileId"" = p.""CompositeProfileId""
                WHERE v.""Id"" = @versionId AND v.""SeriesId"" = @seriesId AND v.""Retired"" = @retired", new { versionId = reservation.SeriesVersionId, seriesId, retired = false }, transaction);
            if (active != 1)
            {
                throw new InvalidOperationException("The download's series version changed before completion.");
            }

            var grab = connection.Query<DownloadHistory>("SELECT * FROM \"DownloadHistory\" WHERE \"DownloadId\" = @downloadId AND \"DownloadClientId\" = @clientId AND \"SeriesVersionId\" = @versionId AND \"EventType\" = @grabbed ORDER BY \"Date\" DESC", new { downloadId = reservation.DownloadId, clientId = reservation.DownloadClientId.Value, versionId = reservation.SeriesVersionId, grabbed = (int)DownloadHistoryEventType.DownloadGrabbed }, transaction).FirstOrDefault();
            var now = DateTime.UtcNow;
            var history = new DownloadHistory { SeriesId = seriesId, SeriesVersionId = reservation.SeriesVersionId, DownloadClientId = reservation.DownloadClientId.Value, DownloadId = reservation.DownloadId, EventType = DownloadHistoryEventType.DownloadImported, Date = now, SourceTitle = grab?.SourceTitle ?? reservation.DownloadId, Protocol = grab?.Protocol ?? default, IndexerId = grab?.IndexerId ?? 0 };
            history.Data.Add("reservationId", reservationId.ToString());
            connection.Execute("INSERT INTO \"DownloadHistory\" (\"SeriesId\", \"SeriesVersionId\", \"DownloadClientId\", \"DownloadId\", \"EventType\", \"Date\", \"SourceTitle\", \"Protocol\", \"IndexerId\", \"Data\", \"Release\") VALUES (@SeriesId, @SeriesVersionId, @DownloadClientId, @DownloadId, @EventType, @Date, @SourceTitle, @Protocol, @IndexerId, @Data, @Release)", history, transaction);
            connection.Execute("INSERT INTO \"EpisodeVersionDownloadCompletions\" (\"ReservationId\", \"CreatedAt\") VALUES (@reservationId, @now)", new { reservationId, now }, transaction);
            // Retain the original release key for audit while freeing the legacy
            // unique key as well as the multi-key reservation locks.
            connection.Execute("UPDATE \"EpisodeDownloadReservations\" SET \"State\" = @imported, \"OriginalReleaseKey\" = \"ReleaseKey\", \"ReleaseKey\" = @archiveKey, \"UpdatedAt\" = @now WHERE \"Id\" = @reservationId", new { imported = (int)EpisodeDownloadReservationState.Imported, archiveKey = "completed:" + reservationId + ":" + Guid.NewGuid().ToString("N"), now, reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservationKeys\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            transaction.Commit();
            return true;
        }
    }
}
