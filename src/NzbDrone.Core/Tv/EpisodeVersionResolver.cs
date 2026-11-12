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
    public class ResolvedEpisodeVersion
    {
        public int EpisodeId { get; set; }
        public int SeriesId { get; set; }
        public int QualityProfileId { get; set; }
        public int ParentQualityProfileId { get; set; }
        public int? SeriesVersionId { get; set; }
        public int? CompositeProfileVersionId { get; set; }
        public int? EpisodeFileId { get; set; }
        public string Label { get; set; }
        public int Priority { get; set; }
        public bool IsPrimary { get; set; }
        public bool Monitored { get; set; }
    }

    public interface IEpisodeVersionResolver
    {
        List<ResolvedEpisodeVersion> Resolve(int episodeId);
        Dictionary<int, List<ResolvedEpisodeVersion>> ResolveMany(IReadOnlyCollection<int> episodeIds);
    }

    public class EpisodeVersionResolver : IEpisodeVersionResolver
    {
        private readonly IMainDatabase _database;

        public EpisodeVersionResolver(IMainDatabase database)
        {
            _database = database;
        }

        public List<ResolvedEpisodeVersion> Resolve(int episodeId)
        {
            return ResolveMany(new[] { episodeId })[episodeId];
        }

        public Dictionary<int, List<ResolvedEpisodeVersion>> ResolveMany(IReadOnlyCollection<int> episodeIds)
        {
            if (episodeIds == null || episodeIds.Count == 0 || episodeIds.Any(id => id <= 0) || episodeIds.Distinct().Count() != episodeIds.Count)
            {
                throw new ArgumentException("A nonempty set of distinct episode IDs is required.", nameof(episodeIds));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            var result = episodeIds.ToDictionary(id => id, id => Resolve(connection, transaction, id));
            transaction.Commit();
            return result;
        }

        private static List<ResolvedEpisodeVersion> Resolve(IDbConnection connection, IDbTransaction transaction, int episodeId)
        {
            var episode = connection.QuerySingleOrDefault<EpisodeContext>("SELECT e.\"Id\" AS \"EpisodeId\", e.\"SeriesId\", e.\"EpisodeFileId\", e.\"Monitored\" AS \"EpisodeMonitored\", s.\"Monitored\" AS \"SeriesMonitored\", s.\"QualityProfileId\" FROM \"Episodes\" e JOIN \"Series\" s ON s.\"Id\" = e.\"SeriesId\" WHERE e.\"Id\" = @episodeId", new { episodeId }, transaction);
            if (episode == null)
            {
                throw new InvalidOperationException("The episode or its series no longer exists.");
            }

            var definitions = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @QualityProfileId ORDER BY \"Priority\"", episode, transaction).ToList();
            if (definitions.Count == 0)
            {
                return new List<ResolvedEpisodeVersion>
                {
                    new ResolvedEpisodeVersion { EpisodeId = episodeId, SeriesId = episode.SeriesId, QualityProfileId = episode.QualityProfileId, ParentQualityProfileId = episode.QualityProfileId, EpisodeFileId = episode.EpisodeFileId == 0 ? null : episode.EpisodeFileId, IsPrimary = true, Monitored = episode.SeriesMonitored && episode.EpisodeMonitored }
                };
            }

            new CompositeProfileValidator().ValidateAndThrow(definitions);
            var rows = connection.Query<VersionContext>(@"SELECT sv.""Id"" AS ""SeriesVersionId"", sv.""CompositeProfileVersionId"", a.""VersionEpisodeFileId"", vf.""SeriesVersionId"" AS ""FileSeriesVersionId"", vf.""EpisodeFileId"", f.""SeriesId"" AS ""FileSeriesId""
                FROM ""SeriesVersions"" sv
                LEFT JOIN ""EpisodeVersionAssignments"" a ON a.""SeriesVersionId"" = sv.""Id"" AND a.""EpisodeId"" = @episodeId
                LEFT JOIN ""VersionEpisodeFiles"" vf ON vf.""Id"" = a.""VersionEpisodeFileId""
                LEFT JOIN ""EpisodeFiles"" f ON f.""Id"" = vf.""EpisodeFileId""
                WHERE sv.""SeriesId"" = @seriesId AND sv.""Retired"" = @retired", new { episodeId, seriesId = episode.SeriesId, retired = false }, transaction).ToList();
            if (rows.Count != definitions.Count || !rows.Select(r => r.CompositeProfileVersionId).ToHashSet().SetEquals(definitions.Select(d => d.Id)) ||
                rows.Any(r => r.VersionEpisodeFileId.HasValue && (!r.EpisodeFileId.HasValue || r.FileSeriesVersionId != r.SeriesVersionId || r.FileSeriesId != episode.SeriesId)))
            {
                throw new InvalidOperationException("The series version assignments are incomplete or inconsistent.");
            }

            var result = definitions.Select(definition =>
            {
                var row = rows.Single(r => r.CompositeProfileVersionId == definition.Id);
                return new ResolvedEpisodeVersion
                {
                    EpisodeId = episodeId,
                    SeriesId = episode.SeriesId,
                    QualityProfileId = definition.QualityProfileId,
                    ParentQualityProfileId = episode.QualityProfileId,
                    SeriesVersionId = row.SeriesVersionId,
                    CompositeProfileVersionId = definition.Id,
                    EpisodeFileId = row.EpisodeFileId,
                    Label = definition.Label,
                    Priority = definition.Priority,
                    IsPrimary = definition.IsPrimary,
                    Monitored = episode.SeriesMonitored && episode.EpisodeMonitored
                };
            }).ToList();
            return result;
        }

        private class EpisodeContext
        {
            public int EpisodeId { get; set; }
            public int SeriesId { get; set; }
            public int QualityProfileId { get; set; }
            public int EpisodeFileId { get; set; }
            public bool EpisodeMonitored { get; set; }
            public bool SeriesMonitored { get; set; }
        }

        private class VersionContext
        {
            public int SeriesVersionId { get; set; }
            public int CompositeProfileVersionId { get; set; }
            public int? VersionEpisodeFileId { get; set; }
            public int? FileSeriesVersionId { get; set; }
            public int? EpisodeFileId { get; set; }
            public int? FileSeriesId { get; set; }
        }
    }
}
