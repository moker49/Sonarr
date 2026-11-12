using System;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Download
{
    public interface IEpisodeVersionGrabRecorder
    {
        void Record(int reservationId, EpisodeGrabbedEvent message);
    }

    public class EpisodeVersionGrabRecorder : IEpisodeVersionGrabRecorder
    {
        private readonly IMainDatabase _database;

        public EpisodeVersionGrabRecorder(IMainDatabase database) => _database = database;

        public void Record(int reservationId, EpisodeGrabbedEvent message)
        {
            if (message?.Episode?.Series == null || message.Episode.SeriesVersionId == null || message.Episode.ParsedEpisodeInfo == null || message.Episode.Release == null ||
                message.Episode.Episodes == null || message.Episode.Episodes.Count == 0 || message.Episode.Episodes.Any(e => e == null || e.SeriesId != message.Episode.Series.Id) ||
                message.DownloadClientId <= 0 || string.IsNullOrWhiteSpace(message.DownloadId) || message.DownloadId.Length > 255 || message.DownloadId != message.DownloadId.Trim())
            {
                throw new ArgumentException("A version release, episode coverage and valid bound client identity are required.");
            }
            var ids = message.Episode.Episodes.Select(e => e.Id).ToArray();
            if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            {
                throw new ArgumentException("Distinct episode identities are required.");
            }
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var seriesId = message.Episode.Series.Id;
            if (connection.Execute("UPDATE \"Series\" SET \"Id\" = \"Id\" WHERE \"Id\" = @seriesId", new { seriesId }, transaction) != 1)
            {
                throw new InvalidOperationException("The grabbed series no longer exists.");
            }
            var reservation = connection.QuerySingleOrDefault<EpisodeDownloadReservation>("SELECT * FROM \"EpisodeDownloadReservations\" WHERE \"Id\" = @reservationId AND \"SeriesId\" = @seriesId", new { reservationId, seriesId }, transaction);
            if (reservation == null || reservation.SeriesVersionId != message.Episode.SeriesVersionId || reservation.DownloadClientId != message.DownloadClientId ||
                (reservation.State != EpisodeDownloadReservationState.Dispatching && reservation.State != EpisodeDownloadReservationState.Submitted) ||
                (reservation.DownloadId != null && !string.Equals(reservation.DownloadId, message.DownloadId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The grabbed client or version reservation changed.");
            }
            var coverage = connection.Query<int>("SELECT \"EpisodeId\" FROM \"EpisodeDownloadReservationEpisodes\" WHERE \"ReservationId\" = @reservationId", new { reservationId }, transaction).ToHashSet();
            if (!coverage.SetEquals(ids))
            {
                throw new InvalidOperationException("Grab history must cover exactly the reserved episodes.");
            }
            if (reservation.GrabRecordedAt.HasValue)
            {
                if (reservation.State != EpisodeDownloadReservationState.Submitted || reservation.DownloadId == null)
                {
                    throw new InvalidOperationException("The recorded grab has inconsistent binding state.");
                }
                return;
            }

            var updated = connection.Execute("UPDATE \"EpisodeDownloadReservations\" SET \"State\" = @submitted, \"DownloadId\" = @downloadId, \"NormalizedDownloadId\" = @normalized, \"GrabRecordedAt\" = @now, \"UpdatedAt\" = @now WHERE \"Id\" = @reservationId", new { submitted = (int)EpisodeDownloadReservationState.Submitted, downloadId = message.DownloadId, normalized = message.DownloadId.ToUpperInvariant(), now = DateTime.UtcNow, reservationId }, transaction);
            if (updated != 1)
            {
                throw new InvalidOperationException("The reservation could not be bound.");
            }
            foreach (var history in HistoryService.CreateGrabbedHistory(message))
            {
                connection.Execute("INSERT INTO \"History\" (\"EpisodeId\", \"SeriesId\", \"SeriesVersionId\", \"EventType\", \"Date\", \"Quality\", \"Languages\", \"SourceTitle\", \"DownloadId\", \"Data\") VALUES (@EpisodeId, @SeriesId, @SeriesVersionId, @EventType, @Date, @Quality, @Languages, @SourceTitle, @DownloadId, @Data)", history, transaction);
            }
            var downloadHistory = DownloadHistoryService.CreateGrabbedHistory(message);
            connection.Execute("INSERT INTO \"DownloadHistory\" (\"SeriesId\", \"SeriesVersionId\", \"DownloadClientId\", \"DownloadId\", \"EventType\", \"Date\", \"SourceTitle\", \"Protocol\", \"IndexerId\", \"Data\", \"Release\") VALUES (@SeriesId, @SeriesVersionId, @DownloadClientId, @DownloadId, @EventType, @Date, @SourceTitle, @Protocol, @IndexerId, @Data, @Release)", downloadHistory, transaction);
            transaction.Commit();
        }
    }
}
