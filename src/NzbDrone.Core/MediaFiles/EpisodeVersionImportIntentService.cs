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
        List<EpisodeVersionImportIntent> GetActive(int seriesId);
        List<int> GetEpisodeIds(int importId);
        void CancelPrepared(int importId);
        void BeginMove(int importId, bool copyOnly);
        void ConfirmMoved(int importId);
        void RecoverMoved(int seriesId, int importId, string note);
        void MarkUncertain(int importId, string note);
    }

    // Persists intent and move evidence; the service never moves or deletes files.
    public class EpisodeVersionImportIntentService : IEpisodeVersionImportIntentService
    {
        private readonly IMainDatabase _database;
        private readonly IDiskProvider _disk;
        private readonly IEpisodeImportFileFingerprintService _fingerprints;

        public EpisodeVersionImportIntentService(IMainDatabase database, IDiskProvider disk, IEpisodeImportFileFingerprintService fingerprints)
        {
            _database = database;
            _disk = disk;
            _fingerprints = fingerprints;
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

        public List<EpisodeVersionImportIntent> GetActive(int seriesId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"SeriesId\" = @seriesId AND \"State\" IN @states ORDER BY \"Id\"", new { seriesId, states = new[] { (int)EpisodeVersionImportIntentState.Prepared, (int)EpisodeVersionImportIntentState.Moving, (int)EpisodeVersionImportIntentState.Moved, (int)EpisodeVersionImportIntentState.Uncertain } }).ToList();
        }

        public List<int> GetEpisodeIds(int importId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<int>("SELECT \"EpisodeId\" FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"ImportId\" = @importId ORDER BY \"EpisodeId\"", new { importId }).ToList();
        }

        public void BeginMove(int importId, bool copyOnly)
        {
            EpisodeVersionImportIntent snapshot;
            using (var connection = _database.OpenConnection())
            {
                snapshot = connection.QuerySingleOrDefault<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId", new { importId });
            }

            if (snapshot == null || snapshot.State != EpisodeVersionImportIntentState.Prepared || !_disk.FileExists(snapshot.SourcePath) || _disk.FileExists(snapshot.DestinationPath))
            {
                throw new InvalidOperationException("The prepared source or destination changed before the move.");
            }

            var evidence = _fingerprints.Read(snapshot.SourcePath);
            if (evidence == null || evidence.Size <= 0 || evidence.Hash?.Length != 64 || evidence.Hash.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new InvalidOperationException("The source fingerprint is unavailable.");
            }

            using var writeConnection = _database.OpenConnection();
            using var transaction = writeConnection.BeginTransaction(IsolationLevel.ReadCommitted);
            var intent = LockAndRead(writeConnection, transaction, importId);
            var reservationValid = writeConnection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId AND \"SeriesVersionId\" = @versionId AND \"State\" = @submitted", new { reservationId = intent.ReservationId, seriesId = intent.SeriesId, versionId = intent.SeriesVersionId, submitted = (int)EpisodeDownloadReservationState.Submitted }, transaction);
            var episodeLocks = writeConnection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"ImportId\" = @importId AND \"SeriesVersionId\" = @versionId", new { importId, versionId = intent.SeriesVersionId }, transaction);
            var expectedHashes = new[] { intent.SourcePath, intent.DestinationPath }.Select(path => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))).ToArray();
            var pathLocks = writeConnection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportPathLocks\" WHERE \"ImportId\" = @importId AND \"PathHash\" IN @expectedHashes", new { importId, expectedHashes }, transaction);
            var validEpisodeLocks = writeConnection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""EpisodeVersionImportEpisodeLocks"" l
                JOIN ""EpisodeDownloadReservationItems"" r ON r.""EpisodeId"" = l.""EpisodeId"" AND r.""SeriesVersionId"" = l.""SeriesVersionId"" AND r.""ReservationId"" = @reservationId
                JOIN ""Episodes"" e ON e.""Id"" = l.""EpisodeId"" AND e.""SeriesId"" = @seriesId
                LEFT JOIN ""EpisodeVersionAssignments"" a ON a.""EpisodeId"" = l.""EpisodeId"" AND a.""SeriesVersionId"" = l.""SeriesVersionId""
                WHERE l.""ImportId"" = @importId AND l.""SeriesVersionId"" = @versionId AND a.""VersionEpisodeFileId"" IS NULL", new { importId, reservationId = intent.ReservationId, seriesId = intent.SeriesId, versionId = intent.SeriesVersionId }, transaction);
            var activeVersion = writeConnection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""SeriesVersions"" sv
                JOIN ""CompositeProfileVersions"" p ON p.""Id"" = sv.""CompositeProfileVersionId""
                JOIN ""Series"" s ON s.""Id"" = sv.""SeriesId"" AND s.""QualityProfileId"" = p.""CompositeProfileId""
                WHERE sv.""Id"" = @versionId AND sv.""SeriesId"" = @seriesId AND sv.""Retired"" = @retired", new { versionId = intent.SeriesVersionId, seriesId = intent.SeriesId, retired = false }, transaction);
            if (intent.State != EpisodeVersionImportIntentState.Prepared || intent.SourcePath != snapshot.SourcePath || intent.DestinationPath != snapshot.DestinationPath ||
                reservationValid != 1 || activeVersion != 1 || episodeLocks == 0 || validEpisodeLocks != episodeLocks || pathLocks != 2 || !_disk.FileExists(intent.SourcePath) || _disk.FileExists(intent.DestinationPath))
            {
                throw new InvalidOperationException("The prepared import changed before the move.");
            }

            writeConnection.Execute("UPDATE \"EpisodeVersionImportIntents\" SET \"State\" = @moving, \"SourceSize\" = @size, \"SourceHash\" = @hash, \"CopyOnly\" = @copyOnly, \"UpdatedAt\" = @now WHERE \"Id\" = @importId", new { moving = (int)EpisodeVersionImportIntentState.Moving, size = evidence.Size, hash = evidence.Hash, copyOnly, now = DateTime.UtcNow, importId }, transaction);
            transaction.Commit();
        }

        public void ConfirmMoved(int importId)
        {
            ConfirmMovedCore(importId, null, null);
        }

        public void RecoverMoved(int seriesId, int importId, string note)
        {
            if (seriesId <= 0 || string.IsNullOrWhiteSpace(note) || note.Length > 2048)
            {
                throw new ArgumentException("A series and recovery explanation are required.");
            }

            ConfirmMovedCore(importId, seriesId, note);
        }

        private void ConfirmMovedCore(int importId, int? expectedSeriesId, string note)
        {
            EpisodeVersionImportIntent snapshot;
            using (var connection = _database.OpenConnection())
            {
                snapshot = connection.QuerySingleOrDefault<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId", new { importId });
            }

            if (snapshot == null || (expectedSeriesId.HasValue && snapshot.SeriesId != expectedSeriesId.Value) ||
                (snapshot.State != EpisodeVersionImportIntentState.Moving && !(expectedSeriesId.HasValue && snapshot.State == EpisodeVersionImportIntentState.Uncertain)) ||
                !snapshot.SourceSize.HasValue || !snapshot.CopyOnly.HasValue || string.IsNullOrWhiteSpace(snapshot.SourceHash) || !_disk.FileExists(snapshot.DestinationPath))
            {
                throw new InvalidOperationException("The move cannot be confirmed without its saved evidence and destination.");
            }

            var target = _fingerprints.Read(snapshot.DestinationPath);
            var sourceExists = _disk.FileExists(snapshot.SourcePath);
            var source = snapshot.CopyOnly.Value && sourceExists ? _fingerprints.Read(snapshot.SourcePath) : null;
            if (target == null || target.Size != snapshot.SourceSize || !string.Equals(target.Hash, snapshot.SourceHash, StringComparison.Ordinal) ||
                (snapshot.CopyOnly.Value ? source == null || source.Size != snapshot.SourceSize || !string.Equals(source.Hash, snapshot.SourceHash, StringComparison.Ordinal) : sourceExists))
            {
                throw new InvalidOperationException("The source and destination do not match the recorded move evidence.");
            }

            using var writeConnection = _database.OpenConnection();
            using var transaction = writeConnection.BeginTransaction(IsolationLevel.ReadCommitted);
            var intent = LockAndRead(writeConnection, transaction, importId);
            if (intent.State != snapshot.State || intent.SeriesId != snapshot.SeriesId || intent.ReservationId != snapshot.ReservationId ||
                intent.SourcePath != snapshot.SourcePath || intent.DestinationPath != snapshot.DestinationPath || intent.SourceSize != snapshot.SourceSize || intent.SourceHash != snapshot.SourceHash || intent.CopyOnly != snapshot.CopyOnly)
            {
                throw new InvalidOperationException("The import changed while verifying the move.");
            }

            var episodeLocks = writeConnection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"ImportId\" = @importId AND \"SeriesVersionId\" = @versionId", new { importId, versionId = intent.SeriesVersionId }, transaction);
            var expectedHashes = new[] { intent.SourcePath, intent.DestinationPath }.Select(path => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))).ToArray();
            var pathLocks = writeConnection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportPathLocks\" WHERE \"ImportId\" = @importId AND \"PathHash\" IN @expectedHashes", new { importId, expectedHashes }, transaction);
            if (episodeLocks == 0 || pathLocks != 2)
            {
                throw new InvalidOperationException("The import's episode or path locks are incomplete.");
            }

            writeConnection.Execute("UPDATE \"EpisodeVersionImportIntents\" SET \"State\" = @moved, \"Note\" = COALESCE(@note, \"Note\"), \"UpdatedAt\" = @now WHERE \"Id\" = @importId", new { moved = (int)EpisodeVersionImportIntentState.Moved, note, now = DateTime.UtcNow, importId }, transaction);
            transaction.Commit();
        }

        public void MarkUncertain(int importId, string note)
        {
            if (string.IsNullOrWhiteSpace(note) || note.Length > 2048)
            {
                throw new ArgumentException("A reconciliation note is required.", nameof(note));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var intent = LockAndRead(connection, transaction, importId);
            if (intent.State != EpisodeVersionImportIntentState.Moving)
            {
                throw new InvalidOperationException("Only a move in progress can be marked uncertain.");
            }

            connection.Execute("UPDATE \"EpisodeVersionImportIntents\" SET \"State\" = @uncertain, \"Note\" = @note, \"UpdatedAt\" = @now WHERE \"Id\" = @importId", new { uncertain = (int)EpisodeVersionImportIntentState.Uncertain, note, now = DateTime.UtcNow, importId }, transaction);
            transaction.Commit();
        }

        private static EpisodeVersionImportIntent LockAndRead(IDbConnection connection, IDbTransaction transaction, int importId)
        {
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId", new { importId }, transaction);
            if (!seriesId.HasValue || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The import or its series no longer exists.");
            }

            return connection.QuerySingle<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId AND \"SeriesId\" = @seriesId", new { importId, seriesId }, transaction);
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
