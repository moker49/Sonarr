using System.Collections.Generic;
using System.Linq;
using System.Net;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.Profiles.Qualities
{
    public static class CompositeProfileSynchronization
    {
        public static readonly object SyncRoot = new object();
    }

    public interface ICompositeProfilePolicy
    {
        void EnsureAssignable(IEnumerable<int> profileIds);
        void EnsureSeriesOrdinary(int seriesId);
        void EnsureSeriesUpdate(int seriesId, int profileId);
        void EnsureDeletable(int profileId);
    }

    public class CompositeProfilePolicy : ICompositeProfilePolicy
    {
        private readonly IMainDatabase _database;

        public CompositeProfilePolicy(IMainDatabase database)
        {
            _database = database;
        }

        public void EnsureAssignable(IEnumerable<int> profileIds)
        {
            var ids = profileIds.Distinct().ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            using var connection = _database.OpenConnection();
            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" IN @ids", new { ids }) != 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Composite profiles are drafts and cannot yet be assigned to series or import lists.");
            }
        }

        public void EnsureSeriesOrdinary(int seriesId)
        {
            using var connection = _database.OpenConnection();
            var ordinary = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Series"" s WHERE s.""Id"" = @seriesId
                AND NOT EXISTS (SELECT 1 FROM ""CompositeProfileVersions"" p WHERE p.""CompositeProfileId"" = s.""QualityProfileId"")
                AND NOT EXISTS (SELECT 1 FROM ""SeriesVersions"" v WHERE v.""SeriesId"" = s.""Id"")", new { seriesId });
            if (ordinary != 1)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "The series is missing or requires an explicit version operation; refresh and try again.");
            }
        }

        public void EnsureSeriesUpdate(int seriesId, int profileId)
        {
            using var connection = _database.OpenConnection();
            var current = connection.QuerySingleOrDefault<int?>("SELECT \"QualityProfileId\" FROM \"Series\" WHERE \"Id\" = @seriesId", new { seriesId });
            if (!current.HasValue)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "The series no longer exists.");
            }
            if (current.Value == profileId)
            {
                return;
            }
            EnsureSeriesOrdinary(seriesId);
            EnsureAssignable(new[] { profileId });
        }

        public void EnsureDeletable(int profileId)
        {
            using var connection = _database.OpenConnection();
            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId OR \"QualityProfileId\" = @profileId", new { profileId }) != 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Remove the composite draft before deleting this profile.");
            }
        }
    }
}
