using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentValidation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Tv
{
    public class SeriesVersionFilePlan
    {
        public int EpisodeFileId { get; set; }
        public int CompositeProfileVersionId { get; set; }
        public List<int> EpisodeIds { get; set; }
    }

    public class SeriesVersionAssignmentPlan
    {
        public int SeriesId { get; set; }
        public int ExistingProfileId { get; set; }
        public int CompositeProfileId { get; set; }
        public List<CompositeProfileVersion> Versions { get; set; }
        public List<SeriesVersionFilePlan> ExistingFiles { get; set; }
    }

    public interface ISeriesVersionAssignmentPlanner
    {
        SeriesVersionAssignmentPlan Preview(int seriesId, int compositeProfileId, int? existingFileVersionId);
    }

    // A preview only. Applying this plan must revalidate the snapshot, active
    // downloads, filesystem, and import journals in the committing transaction.
    public class SeriesVersionAssignmentPlanner : ISeriesVersionAssignmentPlanner
    {
        private readonly IMainDatabase _database;

        public SeriesVersionAssignmentPlanner(IMainDatabase database)
        {
            _database = database;
        }

        public SeriesVersionAssignmentPlan Preview(int seriesId, int compositeProfileId, int? existingFileVersionId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            var currentProfile = connection.QuerySingleOrDefault<int?>("SELECT \"QualityProfileId\" FROM \"Series\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction);
            if (!currentProfile.HasValue)
            {
                throw new InvalidOperationException("The series no longer exists.");
            }

            var definitions = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @compositeProfileId ORDER BY \"Priority\"", new { compositeProfileId }, transaction).ToList();
            new CompositeProfileValidator().ValidateAndThrow(definitions);
            var currentIsComposite = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @currentProfile", new { currentProfile }, transaction) != 0;
            var previousVersions = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"SeriesVersions\" WHERE \"SeriesId\" = @seriesId", new { seriesId }, transaction);
            if (currentIsComposite || previousVersions != 0)
            {
                throw new InvalidOperationException("Only ordinary series without prior version assignments can be previewed.");
            }

            var episodes = connection.Query<EpisodeFileReference>("SELECT \"Id\" AS \"EpisodeId\", \"EpisodeFileId\" FROM \"Episodes\" WHERE \"SeriesId\" = @seriesId AND \"EpisodeFileId\" <> 0", new { seriesId }, transaction).ToList();
            var fileIds = connection.Query<int>("SELECT \"Id\" FROM \"EpisodeFiles\" WHERE \"SeriesId\" = @seriesId", new { seriesId }, transaction).ToHashSet();
            if (!fileIds.SetEquals(episodes.Select(e => e.EpisodeFileId)))
            {
                throw new InvalidOperationException("Registered files and episode references do not match; resolve them before conversion.");
            }

            if (fileIds.Count > 0 && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Episodes\" WHERE \"SeriesId\" <> @seriesId AND \"EpisodeFileId\" IN @fileIds", new { seriesId, fileIds = fileIds.ToArray() }, transaction) != 0)
            {
                throw new InvalidOperationException("A file is referenced by another series; resolve it before conversion.");
            }

            if ((fileIds.Count > 0 && !existingFileVersionId.HasValue) ||
                (existingFileVersionId.HasValue && definitions.All(d => d.Id != existingFileVersionId)))
            {
                throw new InvalidOperationException("Select the destination version for existing files.");
            }

            var result = new SeriesVersionAssignmentPlan
            {
                SeriesId = seriesId,
                ExistingProfileId = currentProfile.Value,
                CompositeProfileId = compositeProfileId,
                Versions = definitions,
                ExistingFiles = episodes.GroupBy(e => e.EpisodeFileId).OrderBy(group => group.Key).Select(group => new SeriesVersionFilePlan
                {
                    EpisodeFileId = group.Key,
                    CompositeProfileVersionId = existingFileVersionId.Value,
                    EpisodeIds = group.Select(e => e.EpisodeId).OrderBy(id => id).ToList()
                }).ToList()
            };
            transaction.Commit();
            return result;
        }

        private class EpisodeFileReference
        {
            public int EpisodeId { get; set; }
            public int EpisodeFileId { get; set; }
        }
    }
}
