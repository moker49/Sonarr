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
    public class EpisodeVersionStatus
    {
        public int EpisodeId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; }
        public bool Monitored { get; set; }
        public int? EpisodeFileId { get; set; }
        public string RelativePath { get; set; }
        public long Size { get; set; }
        public bool IsReserved { get; set; }
    }

    public class SeriesVersionStatus
    {
        public int Id { get; set; }
        public int QualityProfileId { get; set; }
        public string Label { get; set; }
        public int Priority { get; set; }
        public bool IsPrimary { get; set; }
        public List<EpisodeVersionStatus> Episodes { get; set; }
    }

    public interface ISeriesVersionStatusService
    {
        List<SeriesVersionStatus> Get(int seriesId);
    }

    public class SeriesVersionStatusService : ISeriesVersionStatusService
    {
        private readonly IMainDatabase _database;

        public SeriesVersionStatusService(IMainDatabase database)
        {
            _database = database;
        }

        public List<SeriesVersionStatus> Get(int seriesId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            var profileId = connection.QuerySingleOrDefault<int?>("SELECT \"QualityProfileId\" FROM \"Series\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction);
            if (!profileId.HasValue)
            {
                throw new InvalidOperationException("The series no longer exists.");
            }

            var definitions = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId ORDER BY \"Priority\"", new { profileId }, transaction).ToList();
            var versions = connection.Query<SeriesVersion>("SELECT * FROM \"SeriesVersions\" WHERE \"SeriesId\" = @seriesId AND \"Retired\" = @retired", new { seriesId, retired = false }, transaction).ToList();
            if (definitions.Count == 0 && versions.Count == 0)
            {
                transaction.Commit();
                return new List<SeriesVersionStatus>();
            }

            new CompositeProfileValidator().ValidateAndThrow(definitions);
            if (versions.Count != definitions.Count || !versions.Select(v => v.CompositeProfileVersionId).ToHashSet().SetEquals(definitions.Select(d => d.Id)))
            {
                throw new InvalidOperationException("The series version assignments are incomplete or inconsistent.");
            }

            var rows = connection.Query<StatusRow>(@"SELECT sv.""Id"" AS ""SeriesVersionId"", e.""Id"" AS ""EpisodeId"", e.""SeasonNumber"", e.""EpisodeNumber"", e.""Title"",
                    CASE WHEN e.""Monitored"" = @trueValue AND s.""Monitored"" = @trueValue THEN 1 ELSE 0 END AS ""Monitored"",
                    a.""VersionEpisodeFileId"", vf.""SeriesVersionId"" AS ""FileSeriesVersionId"", f.""Id"" AS ""EpisodeFileId"", f.""SeriesId"" AS ""FileSeriesId"", f.""RelativePath"", f.""Size"",
                    CASE WHEN EXISTS (SELECT 1 FROM ""EpisodeDownloadReservationItems"" ri WHERE ri.""SeriesVersionId"" = sv.""Id"" AND ri.""EpisodeId"" = e.""Id"") THEN 1 ELSE 0 END AS ""IsReserved""
                FROM ""SeriesVersions"" sv
                JOIN ""Series"" s ON s.""Id"" = sv.""SeriesId""
                JOIN ""Episodes"" e ON e.""SeriesId"" = s.""Id""
                LEFT JOIN ""EpisodeVersionAssignments"" a ON a.""SeriesVersionId"" = sv.""Id"" AND a.""EpisodeId"" = e.""Id""
                LEFT JOIN ""VersionEpisodeFiles"" vf ON vf.""Id"" = a.""VersionEpisodeFileId""
                LEFT JOIN ""EpisodeFiles"" f ON f.""Id"" = vf.""EpisodeFileId""
                WHERE s.""Id"" = @seriesId AND sv.""Retired"" = @retired
                ORDER BY e.""SeasonNumber"", e.""EpisodeNumber"", e.""Id""", new { seriesId, retired = false, trueValue = true }, transaction).ToList();
            if (rows.Any(r => r.VersionEpisodeFileId.HasValue && (!r.EpisodeFileId.HasValue || r.FileSeriesVersionId != r.SeriesVersionId || r.FileSeriesId != seriesId)))
            {
                throw new InvalidOperationException("An episode file assignment is incomplete or belongs to another version or series.");
            }

            var result = definitions.Select(d =>
            {
                var versionId = versions.Single(v => v.CompositeProfileVersionId == d.Id).Id;
                return new SeriesVersionStatus
                {
                    Id = versionId,
                    QualityProfileId = d.QualityProfileId,
                    Label = d.Label,
                    Priority = d.Priority,
                    IsPrimary = d.IsPrimary,
                    Episodes = rows.Where(r => r.SeriesVersionId == versionId).Select(r => new EpisodeVersionStatus
                    {
                        EpisodeId = r.EpisodeId,
                        SeasonNumber = r.SeasonNumber,
                        EpisodeNumber = r.EpisodeNumber,
                        Title = r.Title,
                        Monitored = r.Monitored,
                        EpisodeFileId = r.EpisodeFileId,
                        RelativePath = r.RelativePath,
                        Size = r.Size,
                        IsReserved = r.IsReserved
                    }).ToList()
                };
            }).ToList();
            transaction.Commit();
            return result;
        }

        private class StatusRow : EpisodeVersionStatus
        {
            public int SeriesVersionId { get; set; }
            public int? VersionEpisodeFileId { get; set; }
            public int? FileSeriesVersionId { get; set; }
            public int? FileSeriesId { get; set; }
        }
    }
}
