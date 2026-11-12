using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Tv
{
    public interface ISeriesVersionAssignmentService
    {
        List<SeriesVersion> Apply(int seriesId, int compositeProfileId, int? existingFileVersionId, string snapshotToken);
    }

    public class SeriesVersionAssignmentService : ISeriesVersionAssignmentService
    {
        private readonly IMainDatabase _database;
        private readonly ITrackedDownloadService _tracked;
        private readonly IDiskProvider _disk;
        private readonly IRootFolderService _roots;
        private readonly ISeriesService _series;
        private readonly IEventAggregator _events;

        public SeriesVersionAssignmentService(IMainDatabase database, ITrackedDownloadService tracked, IDiskProvider disk, IRootFolderService roots, ISeriesService series, IEventAggregator events)
        {
            _database = database;
            _tracked = tracked;
            _disk = disk;
            _roots = roots;
            _series = series;
            _events = events;
        }

        public List<SeriesVersion> Apply(int seriesId, int compositeProfileId, int? existingFileVersionId, string snapshotToken)
        {
            if (seriesId <= 0 || compositeProfileId <= 0 || string.IsNullOrWhiteSpace(snapshotToken))
            {
                throw new ArgumentException("A series, composite profile and preview token are required.");
            }
            using var lease = SeriesOperationSynchronization.Acquire(seriesId);
            List<SeriesVersion> result;
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                if (connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
                {
                    throw new InvalidOperationException("The series no longer exists.");
                }
                var plan = SeriesVersionAssignmentPlanner.Build(connection, transaction, seriesId, compositeProfileId, existingFileVersionId);
                if (!string.Equals(plan.SnapshotToken, snapshotToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The conversion preview is stale; preview the current series again.");
                }
                var profiles = plan.Versions.Select(v => v.QualityProfileId).Append(compositeProfileId).Append(plan.ExistingProfileId).Distinct().OrderBy(id => id).ToArray();
                foreach (var profileId in profiles)
                {
                    if (connection.Execute("UPDATE \"QualityProfiles\" SET \"Id\" = \"Id\" WHERE \"Id\" = @profileId", new { profileId }, transaction) != 1)
                    {
                        throw new InvalidOperationException("A conversion profile no longer exists.");
                    }
                }
                var nested = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" IN @children", new { children = plan.Versions.Select(v => v.QualityProfileId).ToArray() }, transaction);
                var reservations = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeDownloadReservations\" WHERE \"SeriesId\" = @seriesId AND \"State\" IN @states", new { seriesId, states = new[] { (int)EpisodeDownloadReservationState.Prepared, (int)EpisodeDownloadReservationState.Dispatching, (int)EpisodeDownloadReservationState.Submitted, (int)EpisodeDownloadReservationState.Uncertain } }, transaction);
                var imports = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportIntents\" WHERE \"SeriesId\" = @seriesId AND \"State\" NOT IN @terminal", new { seriesId, terminal = new[] { (int)EpisodeVersionImportIntentState.Cancelled, (int)EpisodeVersionImportIntentState.Completed } }, transaction);
                var pending = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"PendingReleases\" WHERE \"SeriesId\" = @seriesId", new { seriesId }, transaction);
                var owned = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionEpisodeFiles\" vf JOIN \"EpisodeFiles\" f ON f.\"Id\" = vf.\"EpisodeFileId\" WHERE f.\"SeriesId\" = @seriesId", new { seriesId }, transaction);
                var jobs = _tracked.GetTrackedDownloads();
                if (jobs == null || nested != 0 || reservations != 0 || imports != 0 || pending != 0 || owned != 0 ||
                    jobs.Any(t => t.IsTrackable && t.State != TrackedDownloadState.Imported && t.State != TrackedDownloadState.Failed && t.State != TrackedDownloadState.Ignored && (t.RemoteEpisode?.Series == null || t.RemoteEpisode.Series.Id == seriesId)))
                {
                    throw new InvalidOperationException("Resolve active downloads, pending releases or unfinished version operations before conversion.");
                }
                var root = _roots.GetBestRootFolderPath(plan.SeriesPath);
                if (string.IsNullOrWhiteSpace(root) || !_disk.FolderExists(root))
                {
                    throw new InvalidOperationException("The series root folder is unavailable.");
                }
                foreach (var file in plan.ExistingFiles)
                {
                    var path = Path.GetFullPath(Path.Combine(plan.SeriesPath, file.RelativePath));
                    if (file.Size <= 0 || Path.IsPathRooted(file.RelativePath) || !Path.GetFullPath(plan.SeriesPath).IsParentPath(path) || !_disk.FileExists(path) || _disk.GetFileSize(path) != file.Size)
                    {
                        throw new InvalidOperationException("An existing file is missing, changed or outside the series folder.");
                    }
                }

                result = new List<SeriesVersion>();
                foreach (var definition in plan.Versions)
                {
                    var versionId = InsertId(connection, transaction, "INSERT INTO \"SeriesVersions\" (\"SeriesId\", \"CompositeProfileVersionId\", \"Retired\") VALUES (@seriesId, @definitionId, @retired)", new { seriesId, definitionId = definition.Id, retired = false });
                    result.Add(new SeriesVersion { Id = versionId, SeriesId = seriesId, CompositeProfileVersionId = definition.Id });
                    var fileOwners = new Dictionary<int, int>();
                    foreach (var file in plan.ExistingFiles.Where(f => f.CompositeProfileVersionId == definition.Id))
                    {
                        var ownerId = InsertId(connection, transaction, "INSERT INTO \"VersionEpisodeFiles\" (\"SeriesVersionId\", \"EpisodeFileId\") VALUES (@versionId, @fileId)", new { versionId, fileId = file.EpisodeFileId });
                        foreach (var episodeId in file.EpisodeIds)
                        {
                            fileOwners.Add(episodeId, ownerId);
                        }
                    }
                    foreach (var episodeId in plan.EpisodeIds)
                    {
                        connection.Execute("INSERT INTO \"EpisodeVersionAssignments\" (\"SeriesVersionId\", \"EpisodeId\", \"VersionEpisodeFileId\") VALUES (@versionId, @episodeId, @ownerId)", new { versionId, episodeId, ownerId = fileOwners.TryGetValue(episodeId, out var ownerId) ? (int?)ownerId : null }, transaction);
                    }
                }
                if (plan.ExistingFiles.Any(f => !plan.Versions.Single(v => v.Id == f.CompositeProfileVersionId).IsPrimary))
                {
                    connection.Execute("UPDATE \"Episodes\" SET \"EpisodeFileId\" = 0 WHERE \"SeriesId\" = @seriesId", new { seriesId }, transaction);
                }
                connection.Execute("UPDATE \"Series\" SET \"QualityProfileId\" = @compositeProfileId WHERE \"Id\" = @seriesId", new { compositeProfileId, seriesId }, transaction);
                transaction.Commit();
            }
            var series = _series.GetSeries(seriesId);
            if (series != null)
            {
                _events.PublishEvent(new SeriesUpdatedEvent(series));
            }
            return result;
        }

        private int InsertId(IDbConnection connection, IDbTransaction transaction, string sql, object parameters)
        {
            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            return connection.ExecuteScalar<int>(sql, parameters, transaction);
        }
    }
}
