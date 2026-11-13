using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeasonSearchService : IExecute<SeasonSearchCommand>
    {
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IManagedEpisodeSearchService _managedSearch;
        private readonly Logger _logger;

        public SeasonSearchService(ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IManagedEpisodeSearchService managedSearch,
                                   Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _managedSearch = managedSearch;
            _logger = logger;
        }

        public void Execute(SeasonSearchCommand message)
        {
            if (_managedSearch.IsManagedSeries(message.SeriesId))
            {
                _managedSearch.SearchMissing(message.SeriesId, true, message.Trigger == CommandTrigger.Manual, message.SeasonNumber).GetAwaiter().GetResult();
                return;
            }

            var decisions = _releaseSearchService.SeasonSearch(message.SeriesId, message.SeasonNumber, false, true, message.Trigger == CommandTrigger.Manual, false).GetAwaiter().GetResult();
            var processed = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();

            _logger.ProgressInfo("Season search completed. {0} reports downloaded.", processed.Grabbed.Count);
        }
    }
}
