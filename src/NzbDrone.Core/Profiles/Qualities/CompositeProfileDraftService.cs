using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentValidation;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileDraftService
    {
        List<CompositeProfileVersion> Get(int profileId);
        HashSet<int> GetProfileIds();
        List<CompositeProfileVersion> Save(int profileId, List<CompositeProfileVersion> versions);
        void Delete(int profileId);
    }

    // Drafts cannot be assigned until version-aware search and import are ready.
    public class CompositeProfileDraftService : ICompositeProfileDraftService
    {
        private readonly IMainDatabase _database;

        public CompositeProfileDraftService(IMainDatabase database)
        {
            _database = database;
        }

        public List<CompositeProfileVersion> Get(int profileId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId ORDER BY \"Priority\"", new { profileId }).ToList();
        }

        public HashSet<int> GetProfileIds()
        {
            using var connection = _database.OpenConnection();
            return connection.Query<int>("SELECT DISTINCT \"CompositeProfileId\" FROM \"CompositeProfileVersions\"").ToHashSet();
        }

        public List<CompositeProfileVersion> Save(int profileId, List<CompositeProfileVersion> versions)
        {
            if (versions == null)
            {
                throw new ArgumentNullException(nameof(versions));
            }

            new CompositeProfileValidator().ValidateAndThrow(versions);
            if (profileId <= 0 || versions.Any(v => v.CompositeProfileId != 0 && v.CompositeProfileId != profileId) || versions.Any(v => v.QualityProfileId == profileId))
            {
                throw new ArgumentException("Versions must belong to the requested profile and cannot reference it.");
            }

            lock (CompositeProfileSynchronization.SyncRoot)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                var profileIds = versions.Select(v => v.QualityProfileId).Append(profileId).Distinct().OrderBy(id => id).ToArray();
                foreach (var id in profileIds)
                {
                    if (connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\" = \"Name\" WHERE \"Id\" = @id", new { id }, transaction) != 1)
                    {
                        throw new InvalidOperationException("A referenced quality profile no longer exists.");
                    }
                }

                EnsureUnused(connection, transaction, profileId);
                var nested = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" IN @childIds OR \"QualityProfileId\" = @profileId", new { childIds = versions.Select(v => v.QualityProfileId).ToArray(), profileId }, transaction);
                if (nested != 0)
                {
                    throw new InvalidOperationException("Composite profiles cannot contain composites or be used as a child.");
                }

                var currentIds = connection.Query<int>("SELECT \"Id\" FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction).OrderBy(id => id).ToArray();
                var submittedIds = versions.Where(v => v.Id != 0).Select(v => v.Id).OrderBy(id => id).ToArray();
                if (!currentIds.SequenceEqual(submittedIds))
                {
                    throw new InvalidOperationException("The composite changed while it was being edited.");
                }

                // Move existing values aside to allow swapping priorities and children
                // without violating the unique indexes during the transaction.
                connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"Priority\" = -\"Id\", \"QualityProfileId\" = -\"Id\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction);
                var result = new List<CompositeProfileVersion>();
                foreach (var version in versions)
                {
                    var saved = new CompositeProfileVersion { Id = version.Id, CompositeProfileId = profileId, QualityProfileId = version.QualityProfileId, Label = version.Label, Priority = version.Priority, IsPrimary = version.IsPrimary };
                    if (saved.Id == 0)
                    {
                        var sql = "INSERT INTO \"CompositeProfileVersions\" (\"CompositeProfileId\", \"QualityProfileId\", \"Label\", \"Priority\", \"IsPrimary\") VALUES (@CompositeProfileId, @QualityProfileId, @Label, @Priority, @IsPrimary)";
                        sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
                        saved.Id = connection.ExecuteScalar<int>(sql, saved, transaction);
                    }
                    else if (connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"QualityProfileId\" = @QualityProfileId, \"Label\" = @Label, \"Priority\" = @Priority, \"IsPrimary\" = @IsPrimary WHERE \"Id\" = @Id AND \"CompositeProfileId\" = @CompositeProfileId", saved, transaction) != 1)
                    {
                        throw new InvalidOperationException("The composite changed while it was being edited.");
                    }

                    result.Add(saved);
                }

                transaction.Commit();
                return result.OrderBy(v => v.Priority).ToList();
            }
        }

        public void Delete(int profileId)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                if (connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\" = \"Name\" WHERE \"Id\" = @profileId", new { profileId }, transaction) != 1)
                {
                    throw new InvalidOperationException("The quality profile no longer exists.");
                }

                EnsureUnused(connection, transaction, profileId);
                connection.Execute("DELETE FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction);
                transaction.Commit();
            }
        }

        private static void EnsureUnused(IDbConnection connection, IDbTransaction transaction, int profileId)
        {
            var assigned = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Series\" WHERE \"QualityProfileId\" = @profileId", new { profileId }, transaction);
            var lists = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"ImportLists\" WHERE \"QualityProfileId\" = @profileId", new { profileId }, transaction);
            var versions = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"SeriesVersions\" v JOIN \"CompositeProfileVersions\" d ON v.\"CompositeProfileVersionId\" = d.\"Id\" WHERE d.\"CompositeProfileId\" = @profileId", new { profileId }, transaction);
            if (assigned != 0 || lists != 0 || versions != 0)
            {
                throw new InvalidOperationException("The profile is in use and cannot be changed as a draft.");
            }
        }
    }
}
