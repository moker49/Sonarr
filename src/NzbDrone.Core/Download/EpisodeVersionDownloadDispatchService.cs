using System;
using System.Linq;
using System.Threading.Tasks;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download
{
    public class EpisodeVersionDownloadSubmission
    {
        public int ReservationId { get; set; }
        public string DownloadId { get; set; }
        public RemoteEpisode Episode { get; set; }
    }

    public interface IEpisodeVersionDownloadDispatchService
    {
        RemoteEpisode Prepare(RemoteEpisode source);
        Task<EpisodeVersionDownloadSubmission> Submit(RemoteEpisode source, IDownloadClient client, IIndexer indexer);
    }

    public class EpisodeVersionDownloadDispatchService : IEpisodeVersionDownloadDispatchService
    {
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly IEpisodeVersionDecisionContext _contexts;
        private readonly IMakeDownloadDecision _decisions;
        private readonly ISeriesService _series;
        private readonly IEpisodeService _episodes;

        public EpisodeVersionDownloadDispatchService(IEpisodeDownloadReservationService reservations, IEpisodeVersionDecisionContext contexts, IMakeDownloadDecision decisions, ISeriesService series, IEpisodeService episodes)
        {
            _reservations = reservations;
            _contexts = contexts;
            _decisions = decisions;
            _series = series;
            _episodes = episodes;
        }

        public RemoteEpisode Prepare(RemoteEpisode source)
        {
            if (source?.Series == null || source.SeriesVersionId == null || source.Release == null || source.Episodes == null || source.Episodes.Count == 0 || source.Episodes.Any(e => e == null || e.Id <= 0))
            {
                throw new InvalidOperationException("An explicit version, release and episode coverage are required.");
            }
            var ids = source.Episodes.Select(e => e.Id).ToArray();
            if (ids.Distinct().Count() != ids.Length)
            {
                throw new InvalidOperationException("The release contains duplicate episode identities.");
            }
            var refreshed = source.CloneForVersion();
            refreshed.Series = _series.GetSeries(source.Series.Id);
            refreshed.Episodes = _episodes.GetEpisodes(ids);
            if (!refreshed.Episodes.Select(e => e.Id).ToHashSet().SetEquals(ids))
            {
                throw new InvalidOperationException("The release's episode coverage changed.");
            }
            var context = _contexts.ForVersion(refreshed, source.SeriesVersionId.Value);
            if (context == null || context.SeriesVersionId != source.SeriesVersionId || context.Episodes.Any(e => e.EpisodeFileId != 0))
            {
                throw new InvalidOperationException("Version downloads currently require empty episode slots.");
            }
            var decision = _decisions.GetMappedDecision(context);
            if (decision == null || !decision.Approved || !context.Episodes.Select(e => e.Id).ToHashSet().SetEquals(ids) || context.Episodes.Any(e => e.EpisodeFileId != 0))
            {
                throw new InvalidOperationException("Final version download validation rejected the release.");
            }
            return context;
        }

        public async Task<EpisodeVersionDownloadSubmission> Submit(RemoteEpisode source, IDownloadClient client, IIndexer indexer)
        {
            if (client?.Definition == null || client.Definition.Id <= 0)
            {
                throw new InvalidOperationException("A configured download client is required.");
            }
            var context = Prepare(source);
            var keys = DownloadReleaseIdentity.GetKeys(context.Release);
            if (keys.Count == 0)
            {
                throw new InvalidOperationException("The release has no stable reservation identity.");
            }
            var reservationId = _reservations.ReserveEmptySlots(context.SeriesVersionId.Value, keys.ToArray(), context.Episodes.Select(e => e.Id).ToArray());
            try
            {
                _reservations.BeginDispatch(reservationId, client.Definition.Id);
            }
            catch
            {
                _reservations.Release(reservationId);
                throw;
            }

            try
            {
                var id = await client.Download(context, indexer);
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("The client accepted the request without a usable download ID.");
                }
                _reservations.Submitted(reservationId, id);
                return new EpisodeVersionDownloadSubmission { ReservationId = reservationId, DownloadId = id, Episode = context };
            }
            catch (Exception ex)
            {
                try
                {
                    _reservations.MarkUncertain(reservationId, "Submission or client binding failed; reconcile the download client before retrying.");
                }
                catch (Exception journalError)
                {
                    throw new AggregateException("Submission failed and uncertainty could not be recorded.", ex, journalError);
                }
                throw;
            }
        }
    }
}
