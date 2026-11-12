using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public interface IEpisodeDownloadReservationService
    {
        int Reserve(int seriesVersionId, string releaseKey, IReadOnlyCollection<int> episodeIds);
        int Reserve(int seriesVersionId, IReadOnlyCollection<string> releaseKeys, IReadOnlyCollection<int> episodeIds);
        List<EpisodeDownloadReservation> ForSeries(int seriesId);
        List<int> GetEpisodeIds(int reservationId);
        EpisodeDownloadReservation FindByClientDownload(int clientId, string downloadId);
        bool HasUnboundForClient(int clientId);
        List<EpisodeDownloadReservation> FindUnboundCandidates(int clientId, IReadOnlyCollection<string> releaseKeys);
        void BeginDispatch(int reservationId, int clientId);
        void Submitted(int reservationId, string downloadId);
        void MarkUncertain(int reservationId, string note);
        void MarkDefinitivelyNotSubmitted(int reservationId, string note);
        void Release(int reservationId);
    }

    // Not connected to dispatch until download-client identity and restart
    // reconciliation are implemented. Reservations must not be cleared merely
    // because a client request timed out.
    public class EpisodeDownloadReservationService : IEpisodeDownloadReservationService, IHandle<ApplicationStartedEvent>
    {
        private readonly IMainDatabase _database;

        public EpisodeDownloadReservationService(IMainDatabase database)
        {
            _database = database;
        }

        public int Reserve(int seriesVersionId, string releaseKey, IReadOnlyCollection<int> episodeIds)
        {
            return Reserve(seriesVersionId, new[] { releaseKey }, episodeIds);
        }

        public int Reserve(int seriesVersionId, IReadOnlyCollection<string> releaseKeys, IReadOnlyCollection<int> episodeIds)
        {
            if (seriesVersionId <= 0 || releaseKeys == null || releaseKeys.Count == 0 ||
                releaseKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 255 || key != key.Trim()) || releaseKeys.Distinct(StringComparer.Ordinal).Count() != releaseKeys.Count ||
                episodeIds == null || episodeIds.Count == 0 || episodeIds.Any(id => id <= 0) || episodeIds.Distinct().Count() != episodeIds.Count)
            {
                throw new ArgumentException("A version, release key and nonempty set of distinct episode IDs are required.");
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"SeriesVersions\" WHERE \"Id\" = @seriesVersionId", new { seriesVersionId }, transaction);
            if (!seriesId.HasValue || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The series version no longer exists.");
            }

            var active = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""SeriesVersions"" sv
                JOIN ""CompositeProfileVersions"" d ON d.""Id"" = sv.""CompositeProfileVersionId""
                JOIN ""Series"" s ON s.""Id"" = sv.""SeriesId"" AND s.""QualityProfileId"" = d.""CompositeProfileId""
                WHERE sv.""Id"" = @seriesVersionId AND sv.""SeriesId"" = @seriesId AND sv.""Retired"" = @retired", new { seriesVersionId, seriesId, retired = false }, transaction);
            var episodes = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Episodes\" WHERE \"SeriesId\" = @seriesId AND \"Id\" IN @episodeIds", new { seriesId, episodeIds }, transaction);
            var occupied = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservationItems\" WHERE \"SeriesVersionId\" = @seriesVersionId AND \"EpisodeId\" IN @episodeIds", new { seriesVersionId, episodeIds }, transaction);
            var duplicate = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservationKeys\" WHERE \"SeriesVersionId\" = @seriesVersionId AND \"ReleaseKey\" IN @releaseKeys", new { seriesVersionId, releaseKeys }, transaction);
            if (active != 1 || episodes != episodeIds.Count || occupied != 0 || duplicate != 0)
            {
                throw new InvalidOperationException("The version or episodes are invalid, or the release overlaps an existing reservation.");
            }

            var sql = "INSERT INTO \"EpisodeDownloadReservations\" (\"SeriesId\", \"SeriesVersionId\", \"ReleaseKey\", \"CreatedAt\") VALUES (@seriesId, @seriesVersionId, @releaseKey, @createdAt)";
            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            var reservationId = connection.ExecuteScalar<int>(sql, new { seriesId, seriesVersionId, releaseKey = releaseKeys.First(), createdAt = DateTime.UtcNow }, transaction);
            foreach (var releaseKey in releaseKeys)
            {
                connection.Execute("INSERT INTO \"EpisodeDownloadReservationKeys\" (\"ReservationId\", \"SeriesVersionId\", \"ReleaseKey\") VALUES (@reservationId, @seriesVersionId, @releaseKey)", new { reservationId, seriesVersionId, releaseKey }, transaction);
            }
            foreach (var episodeId in episodeIds)
            {
                connection.Execute("INSERT INTO \"EpisodeDownloadReservationItems\" (\"ReservationId\", \"SeriesVersionId\", \"EpisodeId\") VALUES (@reservationId, @seriesVersionId, @episodeId)", new { reservationId, seriesVersionId, episodeId }, transaction);
                connection.Execute("INSERT INTO \"EpisodeDownloadReservationEpisodes\" (\"ReservationId\", \"EpisodeId\") VALUES (@reservationId, @episodeId)", new { reservationId, episodeId }, transaction);
            }

            transaction.Commit();
            return reservationId;
        }

        public void Release(int reservationId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId", new { reservationId }, transaction);
            if (!seriesId.HasValue)
            {
                return;
            }

            if (connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The reservation's series no longer exists.");
            }

            var state = connection.QuerySingleOrDefault<int?>("SELECT \"State\" FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId", new { reservationId, seriesId }, transaction);
            if (state.HasValue && state != (int)EpisodeDownloadReservationState.Prepared)
            {
                throw new InvalidOperationException("A dispatched reservation cannot be released without verified reconciliation.");
            }

            connection.Execute("DELETE FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservationEpisodes\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservationKeys\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId", new { reservationId, seriesId }, transaction);
            transaction.Commit();
        }

        public List<EpisodeDownloadReservation> ForSeries(int seriesId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"SeriesId\" = @seriesId ORDER BY \"Id\"", new { seriesId }).ToList();
        }

        public List<int> GetEpisodeIds(int reservationId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<int>("SELECT \"EpisodeId\" FROM \"EpisodeDownloadReservationEpisodes\" WHERE \"ReservationId\" = @reservationId ORDER BY \"EpisodeId\"", new { reservationId }).ToList();
        }

        public EpisodeDownloadReservation FindByClientDownload(int clientId, string downloadId)
        {
            if (clientId <= 0 || string.IsNullOrWhiteSpace(downloadId))
            {
                return null;
            }

            var normalizedId = downloadId.Trim().ToUpperInvariant();
            using var connection = _database.OpenConnection();
            return connection.QuerySingleOrDefault<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"DownloadClientId\" = @clientId AND \"NormalizedDownloadId\" = @normalizedId", new { clientId, normalizedId });
        }

        public List<EpisodeDownloadReservation> FindUnboundCandidates(int clientId, IReadOnlyCollection<string> releaseKeys)
        {
            if (clientId <= 0 || releaseKeys == null || releaseKeys.Count == 0 || releaseKeys.Any(string.IsNullOrWhiteSpace))
            {
                return new List<EpisodeDownloadReservation>();
            }

            using var connection = _database.OpenConnection();
            return connection.Query<EpisodeDownloadReservation>(@"SELECT DISTINCT r.* FROM ""EpisodeDownloadReservations"" r
                JOIN ""EpisodeDownloadReservationKeys"" k ON k.""ReservationId"" = r.""Id""
                WHERE r.""DownloadClientId"" = @clientId AND r.""DownloadId"" IS NULL
                  AND r.""State"" IN @states AND k.""ReleaseKey"" IN @releaseKeys", new
            {
                clientId,
                releaseKeys,
                states = new[] { (int)EpisodeDownloadReservationState.Dispatching, (int)EpisodeDownloadReservationState.Uncertain }
            }).ToList();
        }

        public bool HasUnboundForClient(int clientId)
        {
            if (clientId <= 0)
            {
                return false;
            }

            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservations\" WHERE \"DownloadClientId\" = @clientId AND \"DownloadId\" IS NULL AND \"State\" IN @states", new { clientId, states = new[] { (int)EpisodeDownloadReservationState.Dispatching, (int)EpisodeDownloadReservationState.Uncertain } }) != 0;
        }

        public void BeginDispatch(int reservationId, int clientId)
        {
            if (clientId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clientId));
            }

            ChangeState(reservationId, EpisodeDownloadReservationState.Prepared, EpisodeDownloadReservationState.Dispatching, clientId, null, null);
        }

        public void Submitted(int reservationId, string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId) || downloadId.Length > 255 || downloadId != downloadId.Trim())
            {
                throw new ArgumentException("A valid download-client ID is required.", nameof(downloadId));
            }

            ChangeState(reservationId, EpisodeDownloadReservationState.Dispatching, EpisodeDownloadReservationState.Submitted, null, downloadId, null);
        }

        public void MarkUncertain(int reservationId, string note)
        {
            if (string.IsNullOrWhiteSpace(note) || note.Length > 255)
            {
                throw new ArgumentException("A short explanation is required.", nameof(note));
            }

            ChangeState(reservationId, EpisodeDownloadReservationState.Dispatching, EpisodeDownloadReservationState.Uncertain, null, null, note);
        }

        // Caller must have definitive evidence that no client accepted the job.
        // A timeout, restart, or ambiguous client response is not such evidence.
        public void MarkDefinitivelyNotSubmitted(int reservationId, string note)
        {
            if (string.IsNullOrWhiteSpace(note) || note.Length > 255)
            {
                throw new ArgumentException("A short explanation is required.", nameof(note));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId", new { reservationId }, transaction);
            if (!seriesId.HasValue || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The reservation or its series no longer exists.");
            }

            var changed = connection.Execute("UPDATE \"EpisodeDownloadReservations\" SET \"State\" = @failed, \"UpdatedAt\" = @updatedAt, \"Note\" = @note WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId AND \"DownloadId\" IS NULL AND \"State\" IN @states", new { failed = (int)EpisodeDownloadReservationState.Failed, updatedAt = DateTime.UtcNow, note, reservationId, seriesId, states = new[] { (int)EpisodeDownloadReservationState.Prepared, (int)EpisodeDownloadReservationState.Dispatching } }, transaction);
            if (changed != 1)
            {
                throw new InvalidOperationException("Only a prepared or definitively rejected unbound dispatch can be marked failed.");
            }

            connection.Execute("DELETE FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservationKeys\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            transaction.Commit();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            using var connection = _database.OpenConnection();
            connection.Execute("UPDATE \"EpisodeDownloadReservations\" SET \"State\" = @uncertain, \"UpdatedAt\" = @updatedAt, \"Note\" = @note WHERE \"State\" = @dispatching", new
            {
                uncertain = (int)EpisodeDownloadReservationState.Uncertain,
                dispatching = (int)EpisodeDownloadReservationState.Dispatching,
                updatedAt = DateTime.UtcNow,
                note = "Application restarted during dispatch; verify the download client."
            });
        }

        private void ChangeState(int reservationId, EpisodeDownloadReservationState expected, EpisodeDownloadReservationState target, int? clientId, string downloadId, string note)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId", new { reservationId }, transaction);
            if (!seriesId.HasValue || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The reservation or its series no longer exists.");
            }

            var updated = connection.Execute("UPDATE \"EpisodeDownloadReservations\" SET \"State\" = @target, \"DownloadClientId\" = COALESCE(@clientId, \"DownloadClientId\"), \"DownloadId\" = @downloadId, \"NormalizedDownloadId\" = @normalizedDownloadId, \"UpdatedAt\" = @updatedAt, \"Note\" = @note WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId AND \"State\" = @expected", new { reservationId, seriesId, expected = (int)expected, target = (int)target, clientId, downloadId, normalizedDownloadId = downloadId?.ToUpperInvariant(), updatedAt = DateTime.UtcNow, note }, transaction);
            if (updated != 1)
            {
                throw new InvalidOperationException("The reservation is not in the expected state.");
            }

            transaction.Commit();
        }
    }
}
