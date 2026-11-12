using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public enum EpisodeVersionCompletionKind
    {
        Import = 0,
        Download = 1
    }

    public class EpisodeVersionNotificationDelivery
    {
        public int Id { get; set; }
        public int NotificationId { get; set; }
        public int Attempts { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public string LastError { get; set; }
    }

    public interface IEpisodeVersionNotificationDeliveryService
    {
        void Capture(EpisodeVersionCompletionKind kind, int seriesId, int ownerId, IReadOnlyCollection<int> notificationIds);
        List<EpisodeVersionNotificationDelivery> GetPending(EpisodeVersionCompletionKind kind, int seriesId, int ownerId);
        bool Deliver(EpisodeVersionCompletionKind kind, int seriesId, int ownerId, int notificationId, Action<int> send);
    }

    // At-least-once delivery: a crash after sending but before acknowledgement
    // may repeat that recipient. The stable delivery ID supports deduplication.
    public class EpisodeVersionNotificationDeliveryService : IEpisodeVersionNotificationDeliveryService
    {
        private readonly IMainDatabase _database;

        public EpisodeVersionNotificationDeliveryService(IMainDatabase database)
        {
            _database = database;
        }

        private static string Table(EpisodeVersionCompletionKind kind)
        {
            return kind switch
            {
                EpisodeVersionCompletionKind.Import => "EpisodeVersionImportCompletions",
                EpisodeVersionCompletionKind.Download => "EpisodeVersionDownloadCompletions",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        private static string OwnerColumn(EpisodeVersionCompletionKind kind) => kind == EpisodeVersionCompletionKind.Import ? "ImportId" : "ReservationId";

        private static int FindCompletion(IDbConnection connection, IDbTransaction transaction, EpisodeVersionCompletionKind kind, int seriesId, int ownerId)
        {
            var ownerTable = kind == EpisodeVersionCompletionKind.Import ? "EpisodeVersionImportIntents" : "EpisodeDownloadReservations";
            var id = connection.QuerySingleOrDefault<int?>($"SELECT c.\"Id\" FROM \"{Table(kind)}\" c JOIN \"{ownerTable}\" o ON o.\"Id\" = c.\"{OwnerColumn(kind)}\" WHERE o.\"Id\" = @ownerId AND o.\"SeriesId\" = @seriesId", new { ownerId, seriesId }, transaction);
            return id ?? throw new InvalidOperationException("The completion does not belong to this series.");
        }

        public void Capture(EpisodeVersionCompletionKind kind, int seriesId, int ownerId, IReadOnlyCollection<int> notificationIds)
        {
            if (notificationIds == null || notificationIds.Any(id => id <= 0) || notificationIds.Distinct().Count() != notificationIds.Count)
            {
                throw new ArgumentException("Distinct notification IDs are required.");
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction);
            var completionId = FindCompletion(connection, transaction, kind, seriesId, ownerId);
            var captured = connection.QuerySingle<DateTime?>($"SELECT \"RecipientsCapturedAt\" FROM \"{Table(kind)}\" WHERE \"Id\" = @completionId", new { completionId }, transaction);
            if (captured.HasValue)
            {
                return;
            }

            foreach (var notificationId in notificationIds)
            {
                connection.Execute("INSERT INTO \"EpisodeVersionNotificationDeliveries\" (\"Kind\", \"CompletionId\", \"NotificationId\", \"Attempts\") VALUES (@kind, @completionId, @notificationId, 0)", new { kind = (int)kind, completionId, notificationId }, transaction);
            }
            connection.Execute($"UPDATE \"{Table(kind)}\" SET \"RecipientsCapturedAt\" = @now, \"PublishedAt\" = @published WHERE \"Id\" = @completionId", new { now = DateTime.UtcNow, published = notificationIds.Count == 0 ? (DateTime?)DateTime.UtcNow : null, completionId }, transaction);
            transaction.Commit();
        }

        public List<EpisodeVersionNotificationDelivery> GetPending(EpisodeVersionCompletionKind kind, int seriesId, int ownerId)
        {
            using var connection = _database.OpenConnection();
            var completionId = FindCompletion(connection, null, kind, seriesId, ownerId);
            return connection.Query<EpisodeVersionNotificationDelivery>("SELECT * FROM \"EpisodeVersionNotificationDeliveries\" WHERE \"Kind\" = @kind AND \"CompletionId\" = @completionId AND \"DeliveredAt\" IS NULL ORDER BY \"Id\"", new { kind = (int)kind, completionId }).ToList();
        }

        public bool Deliver(EpisodeVersionCompletionKind kind, int seriesId, int ownerId, int notificationId, Action<int> send)
        {
            if (send == null)
            {
                throw new ArgumentNullException(nameof(send));
            }

            // Serialize local attempts for this completion without holding a
            // database transaction open while a provider performs network I/O.
            lock (EpisodeVersionImportSynchronization.For(ownerId))
            {
                using var connection = _database.OpenConnection();
                var completionId = FindCompletion(connection, null, kind, seriesId, ownerId);
                var delivery = connection.QuerySingleOrDefault<EpisodeVersionNotificationDelivery>("SELECT * FROM \"EpisodeVersionNotificationDeliveries\" WHERE \"Kind\" = @kind AND \"CompletionId\" = @completionId AND \"NotificationId\" = @notificationId", new { kind = (int)kind, completionId, notificationId });
                if (delivery == null)
                {
                    throw new InvalidOperationException("The notification is not a captured recipient.");
                }
                if (delivery.DeliveredAt.HasValue)
                {
                    return true;
                }

                connection.Execute("UPDATE \"EpisodeVersionNotificationDeliveries\" SET \"Attempts\" = \"Attempts\" + 1 WHERE \"Id\" = @id", new { id = delivery.Id });
                try
                {
                    send(delivery.Id);
                }
                catch (Exception ex)
                {
                    var error = ex.Message.Length > 2048 ? ex.Message.Substring(0, 2048) : ex.Message;
                    connection.Execute("UPDATE \"EpisodeVersionNotificationDeliveries\" SET \"LastError\" = @error WHERE \"Id\" = @id", new { error, id = delivery.Id });
                    return false;
                }

                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                connection.Execute("UPDATE \"EpisodeVersionNotificationDeliveries\" SET \"DeliveredAt\" = @now, \"LastError\" = NULL WHERE \"Id\" = @id", new { now = DateTime.UtcNow, id = delivery.Id }, transaction);
                var pending = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionNotificationDeliveries\" WHERE \"Kind\" = @kind AND \"CompletionId\" = @completionId AND \"DeliveredAt\" IS NULL", new { kind = (int)kind, completionId }, transaction);
                if (pending == 0)
                {
                    connection.Execute($"UPDATE \"{Table(kind)}\" SET \"PublishedAt\" = @now WHERE \"Id\" = @completionId", new { now = DateTime.UtcNow, completionId }, transaction);
                }
                transaction.Commit();
                return true;
            }
        }
    }
}
