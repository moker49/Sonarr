using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentValidation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.IndexerSearch
{
    public class WantedEpisodeVersion
    {
        public int SeriesId { get; set; }
        public int EpisodeId { get; set; }
        public int SeasonNumber { get; set; }
        public int SeriesVersionId { get; set; }
        public int CompositeProfileVersionId { get; set; }
        public int QualityProfileId { get; set; }
        public string Label { get; set; }
        public int Priority { get; set; }
    }

    public interface IManagedEpisodeWantedService
    {
        List<WantedEpisodeVersion> Missing(int? seriesId, bool monitored, bool includeSpecials, DateTime beforeUtc);
        List<WantedEpisodeVersion> CutoffUnmet(int? seriesId, bool monitored, bool includeSpecials);
    }

    // This is a read-only selection primitive. It is not dispatched until
    // release decisions and the import pipeline carry the same version identity.
    public class ManagedEpisodeWantedService : IManagedEpisodeWantedService
    {
        private readonly IMainDatabase _database;
        private readonly IQualityProfileService _profiles;

        public ManagedEpisodeWantedService(IMainDatabase database, IQualityProfileService profiles)
        {
            _database = database;
            _profiles = profiles;
        }

        public List<WantedEpisodeVersion> Missing(int? seriesId, bool monitored, bool includeSpecials, DateTime beforeUtc)
        {
            if (beforeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("The cutoff time must be UTC.", nameof(beforeUtc));
            }

            return Select(seriesId, monitored, includeSpecials, beforeUtc, true);
        }

        public List<WantedEpisodeVersion> CutoffUnmet(int? seriesId, bool monitored, bool includeSpecials)
        {
            return Select(seriesId, monitored, includeSpecials, DateTime.UtcNow, false);
        }

        private List<WantedEpisodeVersion> Select(int? seriesId, bool monitored, bool includeSpecials, DateTime beforeUtc, bool missing)
        {
            var profiles = missing ? null : _profiles.All().ToDictionary(p => p.Id);
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var rows = connection.Query<WantedRow>(@"SELECT e.""Id"" AS ""EpisodeId"", e.""SeriesId"", e.""SeasonNumber"", e.""Monitored"" AS ""EpisodeMonitored"", s.""Monitored"" AS ""SeriesMonitored"",
                    d.""Id"" AS ""DefinitionId"", d.""CompositeProfileId"", d.""QualityProfileId"", d.""Label"", d.""Priority"", d.""IsPrimary"",
                    sv.""Id"" AS ""SeriesVersionId"", a.""VersionEpisodeFileId"", vf.""SeriesVersionId"" AS ""FileSeriesVersionId"", vf.""EpisodeFileId"", f.""SeriesId"" AS ""FileSeriesId"", f.""Quality"" AS ""FileQuality"",
                    CASE WHEN EXISTS (SELECT 1 FROM ""EpisodeDownloadReservationItems"" ri WHERE ri.""SeriesVersionId"" = sv.""Id"" AND ri.""EpisodeId"" = e.""Id"") THEN 1 ELSE 0 END AS ""IsReserved"",
                    CASE WHEN EXISTS (SELECT 1 FROM ""SeriesVersions"" other LEFT JOIN ""CompositeProfileVersions"" od ON od.""Id"" = other.""CompositeProfileVersionId"" WHERE other.""SeriesId"" = s.""Id"" AND other.""Retired"" = @retired AND (od.""Id"" IS NULL OR od.""CompositeProfileId"" <> s.""QualityProfileId"")) THEN 1 ELSE 0 END AS ""HasUnexpectedVersion""
                FROM ""Episodes"" e
                JOIN ""Series"" s ON s.""Id"" = e.""SeriesId""
                JOIN ""CompositeProfileVersions"" d ON d.""CompositeProfileId"" = s.""QualityProfileId""
                LEFT JOIN ""SeriesVersions"" sv ON sv.""SeriesId"" = s.""Id"" AND sv.""CompositeProfileVersionId"" = d.""Id"" AND sv.""Retired"" = @retired
                LEFT JOIN ""EpisodeVersionAssignments"" a ON a.""SeriesVersionId"" = sv.""Id"" AND a.""EpisodeId"" = e.""Id""
                LEFT JOIN ""VersionEpisodeFiles"" vf ON vf.""Id"" = a.""VersionEpisodeFileId""
                LEFT JOIN ""EpisodeFiles"" f ON f.""Id"" = vf.""EpisodeFileId""
                WHERE (@seriesId IS NULL OR e.""SeriesId"" = @seriesId)
                    AND (@missing = @falseValue OR (e.""AirDateUtc"" IS NOT NULL AND e.""AirDateUtc"" < @beforeUtc))
                    AND (@includeSpecials = @trueValue OR e.""SeasonNumber"" <> 0)", new { seriesId, beforeUtc, includeSpecials, missing, falseValue = false, trueValue = true, retired = false }, transaction).ToList();

            var result = new List<WantedEpisodeVersion>();
            foreach (var group in rows.GroupBy(r => r.EpisodeId))
            {
                var definitions = group.Select(r => new CompositeProfileVersion { Id = r.DefinitionId, CompositeProfileId = r.CompositeProfileId, QualityProfileId = r.QualityProfileId, Label = r.Label, Priority = r.Priority, IsPrimary = r.IsPrimary }).ToList();
                new CompositeProfileValidator().ValidateAndThrow(definitions);
                if (group.Any(r => r.HasUnexpectedVersion || !r.SeriesVersionId.HasValue ||
                    (r.VersionEpisodeFileId.HasValue && (!r.EpisodeFileId.HasValue || r.FileSeriesVersionId != r.SeriesVersionId || r.FileSeriesId != r.SeriesId))))
                {
                    throw new InvalidOperationException("A series has incomplete or inconsistent version assignments.");
                }

                foreach (var row in group.Where(r => (r.EpisodeMonitored && r.SeriesMonitored) == monitored && !r.IsReserved))
                {
                    if (missing ? row.EpisodeFileId.HasValue : !row.EpisodeFileId.HasValue)
                    {
                        continue;
                    }

                    if (!missing)
                    {
                        if (!profiles.TryGetValue(row.QualityProfileId, out var profile) || row.FileQuality?.Quality == null)
                        {
                            throw new InvalidOperationException("The version's profile or file quality is unavailable.");
                        }

                        var cutoff = profile.UpgradeAllowed ? profile.Cutoff : profile.FirststAllowedQuality().Id;
                        var belowCutoff = profile.Items.Take(profile.GetIndex(cutoff).Index).SelectMany(item => item.GetQualities()).Any(q => q.Id == row.FileQuality.Quality.Id);
                        if (!belowCutoff)
                        {
                            continue;
                        }
                    }

                    result.Add(new WantedEpisodeVersion
                    {
                        SeriesId = row.SeriesId,
                        EpisodeId = row.EpisodeId,
                        SeasonNumber = row.SeasonNumber,
                        SeriesVersionId = row.SeriesVersionId.Value,
                        CompositeProfileVersionId = row.DefinitionId,
                        QualityProfileId = row.QualityProfileId,
                        Label = row.Label,
                        Priority = row.Priority
                    });
                }
            }

            transaction.Commit();
            return result.OrderBy(r => r.SeriesId).ThenBy(r => r.SeasonNumber).ThenBy(r => r.Priority).ThenBy(r => r.EpisodeId).ToList();
        }

        private class WantedRow
        {
            public int EpisodeId { get; set; }
            public int SeriesId { get; set; }
            public int SeasonNumber { get; set; }
            public bool EpisodeMonitored { get; set; }
            public bool SeriesMonitored { get; set; }
            public int DefinitionId { get; set; }
            public int CompositeProfileId { get; set; }
            public int QualityProfileId { get; set; }
            public string Label { get; set; }
            public int Priority { get; set; }
            public bool IsPrimary { get; set; }
            public int? SeriesVersionId { get; set; }
            public int? VersionEpisodeFileId { get; set; }
            public int? FileSeriesVersionId { get; set; }
            public int? EpisodeFileId { get; set; }
            public int? FileSeriesId { get; set; }
            public QualityModel FileQuality { get; set; }
            public bool IsReserved { get; set; }
            public bool HasUnexpectedVersion { get; set; }
        }
    }
}
