using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Tv
{
    public interface IVersionEpisodeFileAssignmentService
    {
        void AssignUnoccupiedFile(int seriesVersionId, int episodeFileId, IReadOnlyCollection<int> episodeIds);
    }

    public class VersionEpisodeFileAssignmentService : IVersionEpisodeFileAssignmentService
    {
        private readonly IMainDatabase _database;

        public VersionEpisodeFileAssignmentService(IMainDatabase database)
        {
            _database = database;
        }

        // This is intentionally limited to empty slots. Replacement requires its own
        // journaled operation because old files may also cover other episodes.
        public void AssignUnoccupiedFile(int seriesVersionId, int episodeFileId, IReadOnlyCollection<int> episodeIds)
        {
            if (episodeIds == null || episodeIds.Count == 0 || episodeIds.Any(id => id <= 0) || episodeIds.Distinct().Count() != episodeIds.Count)
            {
                throw new ArgumentException("A nonempty set of distinct episode IDs is required.", nameof(episodeIds));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = connection.QuerySingleOrDefault<int?>("SELECT \"SeriesId\" FROM \"SeriesVersions\" WHERE \"Id\" = @seriesVersionId AND \"Retired\" = @retired", new { seriesVersionId, retired = false }, transaction);
            if (!seriesId.HasValue || connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The active series version no longer exists.");
            }

            // Recheck after taking the series lock so conversion cannot invalidate the snapshot.
            var activeVersion = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"SeriesVersions\" WHERE \"Id\" = @seriesVersionId AND \"SeriesId\" = @seriesId AND \"Retired\" = @retired", new { seriesVersionId, seriesId, retired = false }, transaction);
            var fileCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeFiles\" WHERE \"Id\" = @episodeFileId AND \"SeriesId\" = @seriesId", new { episodeFileId, seriesId }, transaction);
            var episodeCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Episodes\" WHERE \"SeriesId\" = @seriesId AND \"Id\" IN @episodeIds", new { seriesId, episodeIds }, transaction);
            var existingOwner = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionEpisodeFiles\" WHERE \"EpisodeFileId\" = @episodeFileId", new { episodeFileId }, transaction);
            var occupied = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionAssignments\" WHERE \"SeriesVersionId\" = @seriesVersionId AND \"EpisodeId\" IN @episodeIds AND \"VersionEpisodeFileId\" IS NOT NULL", new { seriesVersionId, episodeIds }, transaction);
            if (activeVersion != 1 || fileCount != 1 || episodeCount != episodeIds.Count || existingOwner != 0 || occupied != 0)
            {
                throw new InvalidOperationException("The version, file, or episode assignments changed or belong to another series.");
            }

            var insertFile = "INSERT INTO \"VersionEpisodeFiles\" (\"SeriesVersionId\", \"EpisodeFileId\") VALUES (@seriesVersionId, @episodeFileId)";
            insertFile += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            var versionFileId = connection.ExecuteScalar<int>(insertFile, new { seriesVersionId, episodeFileId }, transaction);
            foreach (var episodeId in episodeIds)
            {
                var updated = connection.Execute("UPDATE \"EpisodeVersionAssignments\" SET \"VersionEpisodeFileId\" = @versionFileId WHERE \"SeriesVersionId\" = @seriesVersionId AND \"EpisodeId\" = @episodeId AND \"VersionEpisodeFileId\" IS NULL", new { versionFileId, seriesVersionId, episodeId }, transaction);
                if (updated == 0)
                {
                    connection.Execute("INSERT INTO \"EpisodeVersionAssignments\" (\"SeriesVersionId\", \"EpisodeId\", \"VersionEpisodeFileId\") VALUES (@seriesVersionId, @episodeId, @versionFileId)", new { seriesVersionId, episodeId, versionFileId }, transaction);
                }
            }

            transaction.Commit();
        }
    }
}
