using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using NzbDrone.Common;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionImportCompletionService
    {
        int Complete(int seriesId, int importId);
    }

    public class EpisodeVersionImportCompletionService : IEpisodeVersionImportCompletionService
    {
        private readonly IMainDatabase _database;
        private readonly IEpisodeImportFileFingerprintService _fingerprints;

        public EpisodeVersionImportCompletionService(IMainDatabase database, IEpisodeImportFileFingerprintService fingerprints)
        {
            _database = database;
            _fingerprints = fingerprints;
        }

        public int Complete(int seriesId, int importId)
        {
            lock (EpisodeVersionImportSynchronization.For(importId))
            {
                using var connection = _database.OpenConnection();
                var snapshot = connection.QuerySingleOrDefault<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId AND \"SeriesId\" = @seriesId", new { importId, seriesId });
                if (snapshot == null || !snapshot.RegisteredFileId.HasValue || (snapshot.State != EpisodeVersionImportIntentState.Registered && snapshot.State != EpisodeVersionImportIntentState.Completed))
                {
                    throw new InvalidOperationException("Only a registered import can be completed.");
                }

                // Completed retries do not depend on a file remaining on disk forever.
                if (snapshot.State == EpisodeVersionImportIntentState.Completed)
                {
                    return snapshot.RegisteredFileId.Value;
                }

                var actual = _fingerprints.Read(snapshot.DestinationPath);
                if (actual == null || actual.Size != snapshot.SourceSize || actual.Hash != snapshot.SourceHash)
                {
                    throw new InvalidOperationException("The registered destination no longer matches the import evidence.");
                }

                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                if (connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
                {
                    throw new InvalidOperationException("The import's series no longer exists.");
                }
                var intent = connection.QuerySingle<EpisodeVersionImportIntent>("SELECT * FROM \"EpisodeVersionImportIntents\" WHERE \"Id\" = @importId AND \"SeriesId\" = @seriesId", new { importId, seriesId }, transaction);
                if (intent.State != snapshot.State || intent.RegisteredFileId != snapshot.RegisteredFileId || intent.SourceHash != snapshot.SourceHash || intent.SourceSize != snapshot.SourceSize || intent.DestinationPath != snapshot.DestinationPath)
                {
                    throw new InvalidOperationException("The import changed while completing it.");
                }

                var file = connection.QuerySingleOrDefault<EpisodeFile>("SELECT * FROM \"EpisodeFiles\" WHERE \"Id\" = @fileId AND \"SeriesId\" = @seriesId", new { fileId = intent.RegisteredFileId.Value, seriesId }, transaction);
                var seriesPath = connection.QuerySingle<string>("SELECT \"Path\" FROM \"Series\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction);
                var ids = connection.Query<int>("SELECT \"EpisodeId\" FROM \"EpisodeVersionImportEpisodes\" WHERE \"ImportId\" = @importId ORDER BY \"EpisodeId\"", new { importId }, transaction).ToArray();
                var assigned = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""EpisodeVersionAssignments"" a
                    JOIN ""VersionEpisodeFiles"" vf ON vf.""Id"" = a.""VersionEpisodeFileId"" AND vf.""SeriesVersionId"" = a.""SeriesVersionId""
                    JOIN ""Episodes"" e ON e.""Id"" = a.""EpisodeId"" AND e.""SeriesId"" = @seriesId
                    WHERE a.""SeriesVersionId"" = @versionId AND vf.""EpisodeFileId"" = @fileId AND a.""EpisodeId"" IN @ids", new { seriesId, versionId = intent.SeriesVersionId, fileId = intent.RegisteredFileId.Value, ids }, transaction);
                var locks = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"ImportId\" = @importId AND \"SeriesVersionId\" = @versionId AND \"EpisodeId\" IN @ids", new { importId, versionId = intent.SeriesVersionId, ids }, transaction);
                var reservation = connection.QuerySingleOrDefault<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId AND \"SeriesVersionId\" = @versionId", new { reservationId = intent.ReservationId, seriesId, versionId = intent.SeriesVersionId }, transaction);
                var versionValid = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""SeriesVersions"" v
                    JOIN ""CompositeProfileVersions"" p ON p.""Id"" = v.""CompositeProfileVersionId""
                    JOIN ""Series"" s ON s.""Id"" = v.""SeriesId"" AND s.""QualityProfileId"" = p.""CompositeProfileId""
                    WHERE v.""Id"" = @versionId AND v.""SeriesId"" = @seriesId AND v.""Retired"" = @retired", new { versionId = intent.SeriesVersionId, seriesId, retired = false }, transaction);
                var reserved = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservationItems\" WHERE \"ReservationId\" = @reservationId AND \"SeriesVersionId\" = @versionId AND \"EpisodeId\" IN @ids", new { reservationId = intent.ReservationId, versionId = intent.SeriesVersionId, ids }, transaction);
                var hashes = new[] { intent.SourcePath, intent.DestinationPath }.Select(path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())))).ToArray();
                var pathLocks = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportPathLocks\" WHERE \"ImportId\" = @importId AND \"PathHash\" IN @hashes", new { importId, hashes }, transaction);
                if (file == null || file.Size != intent.SourceSize || ids.Length == 0 || assigned != ids.Length || locks != ids.Length || reserved != ids.Length || versionValid != 1 || pathLocks != 2 ||
                    !PathEqualityComparer.Instance.Equals(Path.GetFullPath(Path.Combine(seriesPath, file.RelativePath)), intent.DestinationPath) ||
                    reservation == null || reservation.State != EpisodeDownloadReservationState.Submitted || !reservation.DownloadClientId.HasValue || string.IsNullOrWhiteSpace(reservation.DownloadId))
                {
                    throw new InvalidOperationException("The registered file, episode coverage or reservation changed.");
                }

                var data = new Dictionary<string, string>
                {
                    ["fileId"] = file.Id.ToString(),
                    ["versionImportId"] = importId.ToString(),
                    ["droppedPath"] = intent.SourcePath,
                    ["importedPath"] = intent.DestinationPath,
                    [EpisodeHistory.DOWNLOAD_CLIENT_ID] = reservation.DownloadClientId.Value.ToString(),
                    ["size"] = file.Size.ToString()
                };
                var now = DateTime.UtcNow;
                foreach (var episodeId in ids)
                {
                    var history = new EpisodeHistory { EpisodeId = episodeId, SeriesId = seriesId, SeriesVersionId = intent.SeriesVersionId, EventType = EpisodeHistoryEventType.DownloadFolderImported, Date = now, Quality = file.Quality, Languages = file.Languages, SourceTitle = file.SceneName ?? Path.GetFileNameWithoutExtension(intent.SourcePath), DownloadId = reservation.DownloadId, Data = data };
                    connection.Execute("INSERT INTO \"History\" (\"EpisodeId\", \"SeriesId\", \"SeriesVersionId\", \"EventType\", \"Date\", \"Quality\", \"Languages\", \"SourceTitle\", \"DownloadId\", \"Data\") VALUES (@EpisodeId, @SeriesId, @SeriesVersionId, @EventType, @Date, @Quality, @Languages, @SourceTitle, @DownloadId, @Data)", history, transaction);
                }

                var grab = connection.Query<DownloadHistory>("SELECT * FROM \"DownloadHistory\" WHERE \"DownloadId\" = @downloadId AND \"DownloadClientId\" = @clientId AND \"SeriesVersionId\" = @versionId AND \"EventType\" = @grabbed ORDER BY \"Date\" DESC", new { downloadId = reservation.DownloadId, clientId = reservation.DownloadClientId.Value, versionId = intent.SeriesVersionId, grabbed = (int)DownloadHistoryEventType.DownloadGrabbed }, transaction).FirstOrDefault();
                var downloadHistory = new DownloadHistory { SeriesId = seriesId, SeriesVersionId = intent.SeriesVersionId, DownloadId = reservation.DownloadId, DownloadClientId = reservation.DownloadClientId.Value, EventType = DownloadHistoryEventType.FileImported, SourceTitle = intent.SourcePath, Date = now, Data = data, Protocol = grab?.Protocol ?? default, IndexerId = grab?.IndexerId ?? 0 };
                connection.Execute("INSERT INTO \"DownloadHistory\" (\"SeriesId\", \"SeriesVersionId\", \"DownloadId\", \"DownloadClientId\", \"EventType\", \"SourceTitle\", \"Date\", \"Data\", \"Protocol\", \"IndexerId\", \"Release\") VALUES (@SeriesId, @SeriesVersionId, @DownloadId, @DownloadClientId, @EventType, @SourceTitle, @Date, @Data, @Protocol, @IndexerId, @Release)", downloadHistory, transaction);
                connection.Execute("INSERT INTO \"EpisodeVersionImportCompletions\" (\"ImportId\", \"CreatedAt\") VALUES (@importId, @now)", new { importId, now }, transaction);
                connection.Execute("UPDATE \"EpisodeVersionImportIntents\" SET \"State\" = @completed, \"UpdatedAt\" = @now WHERE \"Id\" = @importId", new { completed = (int)EpisodeVersionImportIntentState.Completed, now, importId }, transaction);
                connection.Execute("DELETE FROM \"EpisodeVersionImportEpisodeLocks\" WHERE \"ImportId\" = @importId", new { importId }, transaction);
                connection.Execute("DELETE FROM \"EpisodeVersionImportPathLocks\" WHERE \"ImportId\" = @importId", new { importId }, transaction);
                transaction.Commit();
                return file.Id;
            }
        }
    }
}
