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
