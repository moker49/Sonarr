using System;
using System.Collections.Generic;
using Dapper;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles
{
    public class PendingEpisodeVersionNotification
    {
        public EpisodeVersionCompletionKind Kind { get; set; }
        public int SeriesId { get; set; }
        public int OwnerId { get; set; }
    }

    public interface IEpisodeVersionNotificationQueue
    {
        List<PendingEpisodeVersionNotification> GetDue();
        void RecordAttempt(PendingEpisodeVersionNotification notification);
    }

    public class EpisodeVersionNotificationQueue : IEpisodeVersionNotificationQueue
    {
        private readonly IMainDatabase _database;

        public EpisodeVersionNotificationQueue(IMainDatabase database) => _database = database;

        public List<PendingEpisodeVersionNotification> GetDue()
        {
            using var connection = _database.OpenConnection();
            return connection.Query<PendingEpisodeVersionNotification>(@"SELECT ""Kind"", ""SeriesId"", ""OwnerId"" FROM (
                SELECT 0 AS ""Kind"", i.""SeriesId"", i.""Id"" AS ""OwnerId"", COALESCE(c.""LastAttemptAt"", c.""CreatedAt"") AS ""OrderTime""
                FROM ""EpisodeVersionImportCompletions"" c JOIN ""EpisodeVersionImportIntents"" i ON i.""Id"" = c.""ImportId""
                WHERE c.""PublishedAt"" IS NULL AND (c.""LastAttemptAt"" IS NULL OR c.""LastAttemptAt"" <= @retryBefore)
                UNION ALL
                SELECT 1 AS ""Kind"", r.""SeriesId"", r.""Id"" AS ""OwnerId"", COALESCE(c.""LastAttemptAt"", c.""CreatedAt"") AS ""OrderTime""
                FROM ""EpisodeVersionDownloadCompletions"" c JOIN ""EpisodeDownloadReservations"" r ON r.""Id"" = c.""ReservationId""
                WHERE c.""PublishedAt"" IS NULL AND (c.""LastAttemptAt"" IS NULL OR c.""LastAttemptAt"" <= @retryBefore)
                ) pending ORDER BY ""OrderTime"", ""Kind"", ""OwnerId"" LIMIT 50", new { retryBefore = DateTime.UtcNow.AddMinutes(-5) }).AsList();
        }

        public void RecordAttempt(PendingEpisodeVersionNotification notification)
        {
            var table = notification.Kind switch
            {
                EpisodeVersionCompletionKind.Import => "EpisodeVersionImportCompletions",
                EpisodeVersionCompletionKind.Download => "EpisodeVersionDownloadCompletions",
                _ => throw new ArgumentOutOfRangeException(nameof(notification.Kind))
            };
            var column = notification.Kind == EpisodeVersionCompletionKind.Import ? "ImportId" : "ReservationId";
            using var connection = _database.OpenConnection();
            connection.Execute($"UPDATE \"{table}\" SET \"LastAttemptAt\" = @now WHERE \"{column}\" = @id AND \"PublishedAt\" IS NULL", new { now = DateTime.UtcNow, id = notification.OwnerId });
        }
    }

    public class EpisodeVersionNotificationRetryService : IExecute<RetryEpisodeVersionNotificationsCommand>
    {
        private readonly IEpisodeVersionNotificationQueue _queue;
        private readonly IEpisodeVersionImportNotificationService _imports;
        private readonly IEpisodeVersionDownloadNotificationService _downloads;
        private readonly Logger _logger;

        public EpisodeVersionNotificationRetryService(IEpisodeVersionNotificationQueue queue, IEpisodeVersionImportNotificationService imports, IEpisodeVersionDownloadNotificationService downloads, Logger logger)
        {
            _queue = queue;
            _imports = imports;
            _downloads = downloads;
            _logger = logger;
        }

        public void Execute(RetryEpisodeVersionNotificationsCommand message)
        {
            foreach (var notification in _queue.GetDue())
            {
                try
                {
                    _queue.RecordAttempt(notification);
                    if (notification.Kind == EpisodeVersionCompletionKind.Import)
                    {
                        _imports.Deliver(notification.SeriesId, notification.OwnerId);
                    }
                    else
                    {
                        _downloads.Deliver(notification.SeriesId, notification.OwnerId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to deliver version {0} notifications for {1}; retaining completion for retry", notification.Kind, notification.OwnerId);
                }
            }
        }
    }
}
