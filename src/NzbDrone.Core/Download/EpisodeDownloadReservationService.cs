using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Download
{
    public interface IEpisodeDownloadReservationService
    {
        int Reserve(int seriesVersionId, string releaseKey, IReadOnlyCollection<int> episodeIds);
        void Release(int reservationId);
    }

    // Not connected to dispatch until download-client identity and restart
    // reconciliation are implemented. Reservations must not be cleared merely
    // because a client request timed out.
    public class EpisodeDownloadReservationService : IEpisodeDownloadReservationService
    {
        private readonly IMainDatabase _database;

        public EpisodeDownloadReservationService(IMainDatabase database)
        {
            _database = database;
        }

        public int Reserve(int seriesVersionId, string releaseKey, IReadOnlyCollection<int> episodeIds)
        {
            if (seriesVersionId <= 0 || string.IsNullOrWhiteSpace(releaseKey) || releaseKey.Length > 255 || releaseKey != releaseKey.Trim() ||
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
            var duplicate = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservations\" WHERE \"SeriesVersionId\" = @seriesVersionId AND \"ReleaseKey\" = @releaseKey", new { seriesVersionId, releaseKey }, transaction);
            if (active != 1 || episodes != episodeIds.Count || occupied != 0 || duplicate != 0)
            {
                throw new InvalidOperationException("The version or episodes are invalid, or the release overlaps an existing reservation.");
            }

            var sql = "INSERT INTO \"EpisodeDownloadReservations\" (\"SeriesId\", \"SeriesVersionId\", \"ReleaseKey\", \"CreatedAt\") VALUES (@seriesId, @seriesVersionId, @releaseKey, @createdAt)";
            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            var reservationId = connection.ExecuteScalar<int>(sql, new { seriesId, seriesVersionId, releaseKey, createdAt = DateTime.UtcNow }, transaction);
            foreach (var episodeId in episodeIds)
            {
                connection.Execute("INSERT INTO \"EpisodeDownloadReservationItems\" (\"ReservationId\", \"SeriesVersionId\", \"EpisodeId\") VALUES (@reservationId, @seriesVersionId, @episodeId)", new { reservationId, seriesVersionId, episodeId }, transaction);
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

            connection.Execute("DELETE FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId", new { reservationId, seriesId }, transaction);
            transaction.Commit();
        }
    }
}
