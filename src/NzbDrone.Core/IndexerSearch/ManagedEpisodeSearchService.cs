using System;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    public interface IManagedEpisodeSearchService
    {
        bool IsManagedSeries(int seriesId);
        Task<int> SearchMissing(int? seriesId, bool monitored, bool userInvokedSearch, int? seasonNumber = null);
    }

    public class ManagedEpisodeSearchService : IManagedEpisodeSearchService
    {
        private readonly ISeriesService _series;
        private readonly ICompositeProfileDraftService _composites;
        private readonly IManagedEpisodeWantedService _wanted;
        private readonly IEpisodeService _episodes;
        private readonly IQueueService _queue;
        private readonly ISearchForReleases _search;
        private readonly IProcessDownloadDecisions _downloads;
        private readonly Logger _logger;

        public ManagedEpisodeSearchService(ISeriesService series,
                                          ICompositeProfileDraftService composites,
                                          IManagedEpisodeWantedService wanted,
                                          IEpisodeService episodes,
                                          IQueueService queue,
                                          ISearchForReleases search,
                                          IProcessDownloadDecisions downloads,
                                          Logger logger)
        {
            _series = series;
            _composites = composites;
            _wanted = wanted;
            _episodes = episodes;
            _queue = queue;
            _search = search;
            _downloads = downloads;
            _logger = logger;
        }

        public bool IsManagedSeries(int seriesId)
        {
            return _composites.GetProfileIds().Contains(_series.GetSeries(seriesId).QualityProfileId);
        }

        public async Task<int> SearchMissing(int? seriesId, bool monitored, bool userInvokedSearch, int? seasonNumber = null)
        {
            var wanted = _wanted.Missing(seriesId, monitored, true, DateTime.UtcNow)
                .Where(w => !seasonNumber.HasValue || w.SeasonNumber == seasonNumber.Value)
                .ToList();
            var downloadedCount = 0;

            // Keep child versions separate even when they cover the same episodes.
            // Dispatch sequentially in definition priority order; reservations and
            // final dispatch validation handle changes after this read snapshot.
            foreach (var group in wanted.GroupBy(w => new { w.SeriesId, w.SeasonNumber, w.SeriesVersionId })
                         .OrderBy(g => g.Key.SeriesId).ThenBy(g => g.Min(w => w.Priority)).ThenBy(g => g.Key.SeasonNumber))
            {
                var queue = _queue.GetQueue();
                var episodeIds = group.Select(w => w.EpisodeId).Distinct()
                    .Where(id => !queue.Any(q => q.Episode?.Id == id && (!q.SeriesVersionId.HasValue || q.SeriesVersionId == group.Key.SeriesVersionId)))
                    .ToArray();
                if (episodeIds.Length == 0)
                {
                    continue;
                }

                var episodes = _episodes.GetEpisodes(episodeIds);
                if (episodes.Count != episodeIds.Length || !episodes.Select(e => e.Id).ToHashSet().SetEquals(episodeIds) ||
                    episodes.Any(e => e.SeriesId != group.Key.SeriesId || e.SeasonNumber != group.Key.SeasonNumber))
                {
                    throw new InvalidOperationException("The selected version episode set changed; refresh before searching again.");
                }

                var decisions = episodes.Count == 1
                    ? await _search.EpisodeSearch(episodes[0].Id, userInvokedSearch, false, group.Key.SeriesVersionId)
                    : await _search.SeasonSearch(group.Key.SeriesId, group.Key.SeasonNumber, episodes, monitored, userInvokedSearch, false, group.Key.SeriesVersionId);
                var processed = await _downloads.ProcessDecisions(decisions);
                downloadedCount += processed.Grabbed.Count;
            }

            _logger.ProgressInfo("Completed version-aware missing episode search. {0} reports downloaded.", downloadedCount);
            return downloadedCount;
        }
    }
}
