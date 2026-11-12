using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionImportIntentService
    {
        int Prepare(int reservationId, int clientId, string downloadId, IReadOnlyCollection<int> episodeIds, string sourcePath, string destinationPath);
        List<EpisodeVersionImportIntent> GetPrepared(int seriesId);
        void CancelPrepared(int importId);
    }

    // Persists only the pre-move intent. No file operation is performed by this
    // service; later move/recovery states must be added before imports are enabled.
    public class EpisodeVersionImportIntentService : IEpisodeVersionImportIntentService
    {
        private readonly IMainDatabase _database;
        private readonly IDiskProvider _disk;

        public EpisodeVersionImportIntentService(IMainDatabase database, IDiskProvider disk)
        {
            _database = database;
            _disk = disk;
        }

        public int Prepare(int reservationId, int clientId, string downloadId, IReadOnlyCollection<int> episodeIds, string sourcePath, string destinationPath)
        {
            if (reservationId <= 0 || clientId <= 0 || string.IsNullOrWhiteSpace(downloadId) || episodeIds == null || episodeIds.Count == 0 ||
                episodeIds.Any(id => id <= 0) || episodeIds.Distinct().Count() != episodeIds.Count ||
                string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath) ||
                !Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationPath))
            {
                throw new ArgumentException("A bound reservation, distinct episodes and absolute source and destination paths are required.");
            }

            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
            if (PathEqualityComparer.Instance.Equals(sourcePath, destinationPath))
            {
                throw new InvalidOperationException("The source and destination cannot be the same file.");
            }

            var pathHashes = new[] { sourcePath, destinationPath }.Select(path => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))).Distinct().ToArray();
            if (pathHashes.Length != 2)
            {
                throw new InvalidOperationException("The source and destination cannot share an import path lock.");
            }
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var reservation = connection.QuerySingleOrDefault<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId", new { reservationId }, transaction);
            if (reservation == null || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId = reservation.SeriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The reservation or series no longer exists.");
            }

            // Re-read after taking the series lock, then validate the entire pack.
            var lockedSeriesId = reservation.SeriesId;
            reservation = connection.QuerySingleOrDefault<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId", new { reservationId }, transaction);
            if (reservation == null || reservation.SeriesId != lockedSeriesId || reservation.State != EpisodeDownloadReservationState.Submitted || reservation.DownloadClientId != clientId ||
                !string.Equals(reservation.NormalizedDownloadId, downloadId.Trim().ToUpperInvariant(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The submitted download reservation changed.");
            }

            var seriesId = reservation.SeriesId;
            var versionId = reservation.SeriesVersionId;
            var seriesPath = connection.QuerySingle<string>("SELECT \"Path\" FROM \"Series\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction);
            var activeVersion = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""SeriesVersions"" sv
                JOIN ""CompositeProfileVersions"" p ON p.""Id"" = sv.""CompositeProfileVersionId""
                JOIN ""Series"" s ON s.""Id"" = sv.""SeriesId"" AND s.""QualityProfileId"" = p.""CompositeProfileId""
                WHERE sv.""Id"" = @versionId AND sv.""SeriesId"" = @seriesId AND sv.""Retired"" = @retired", new { versionId, seriesId, retired = false }, transaction);
            var reserved = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId AND \"SeriesVersionId\" = @versionId AND \"EpisodeId\" IN @episodeIds", new { reservationId, versionId, episodeIds }, transaction);
            var belonging = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Episodes\" WHERE \"SeriesId\" = @seriesId AND \"Id\" IN @episodeIds", new { seriesId, episodeIds }, transaction);
            var occupied = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionAssignments\" WHERE \"SeriesVersionId\" = @versionId AND \"EpisodeId\" IN @episodeIds AND \"VersionEpisodeFileId\" IS NOT NULL", new { versionId, episodeIds }, transaction);
            var locked = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"SeriesVersionId\" = @versionId AND \"EpisodeId\" IN @episodeIds", new { versionId, episodeIds }, transaction);
            var pathLocked = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportPathLocks\" WHERE \"PathHash\" IN @pathHashes", new { pathHashes }, transaction);
            var registeredPaths = connection.Query<string>("SELECT \"RelativePath\" FROM \"EpisodeFiles\" WHERE \"SeriesId\" = @seriesId", new { seriesId }, transaction);
            if (activeVersion != 1 || reserved != episodeIds.Count || belonging != episodeIds.Count || occupied != 0 || locked != 0 || pathLocked != 0 ||
                !Path.GetFullPath(seriesPath).IsParentPath(destinationPath) || registeredPaths.Any(p => PathEqualityComparer.Instance.Equals(Path.GetFullPath(Path.Combine(seriesPath, p)), destinationPath)) ||
                !_disk.FileExists(sourcePath) || _disk.FileExists(destinationPath))
            {
                throw new InvalidOperationException("The version, episodes, source or destination cannot be safely prepared for an empty-slot import.");
            }

            var now = DateTime.UtcNow;
            var sql = "INSERT INTO \"EpisodeVersionImportIntents\" (\"SeriesId\", \"SeriesVersionId\", \"ReservationId\", \"SourcePath\", \"DestinationPath\", \"State\", \"CreatedAt\", \"UpdatedAt\") VALUES (@seriesId, @versionId, @reservationId, @sourcePath, @destinationPath, @state, @now, @now)";
            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            var importId = connection.ExecuteScalar<int>(sql, new { seriesId, versionId, reservationId, sourcePath, destinationPath, state = (int)EpisodeVersionImportIntentState.Prepared, now }, transaction);
            foreach (var episodeId in episodeIds)
            {
                connection.Execute("INSERT INTO \"EpisodeVersionImportEpisodeLocks\" (\"ImportId\", \"SeriesVersionId\", \"EpisodeId\") VALUES (@importId, @versionId, @episodeId)", new { importId, versionId, episodeId }, transaction);
            }
            foreach (var pathHash in pathHashes)
            {
                connection.Execute("INSERT INTO \"EpisodeVersionImportPathLocks\" (\"ImportId\", \"PathHash\") VALUES (@importId, @pathHash)", new { importId, pathHash }, transaction);
            }
            transaction.Commit();
            return importId;
        }

        public List<EpisodeVersionImportIntent> GetPrepared(int seriesId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"SeriesId\" = @seriesId AND \"State\" = @state ORDER BY \"Id\"", new { seriesId, state = (int)EpisodeVersionImportIntentState.Prepared }).ToList();
        }

        public void CancelPrepared(int importId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId", new { importId }, transaction);
            if (!seriesId.HasValue || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1 ||
                connection.Execute("UPDATE \"EpisodeVersionImportIntents\" SET \"State\" = @cancelled, \"UpdatedAt\" = @now WHERE \"Id\" = @importId AND \"State\" = @prepared", new { importId, cancelled = (int)EpisodeVersionImportIntentState.Cancelled, prepared = (int)EpisodeVersionImportIntentState.Prepared, now = DateTime.UtcNow }, transaction) != 1)
            {
                throw new InvalidOperationException("Only a prepared import can be cancelled.");
            }

            connection.Execute("DELETE FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"ImportId\" = @importId", new { importId }, transaction);
            connection.Execute("DELETE FROM \"EpisodeVersionImportPathLocks\" WHERE \"ImportId\" = @importId", new { importId }, transaction);
            transaction.Commit();
        }
    }
}
