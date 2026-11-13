using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class ManagedSeasonSearchCommandFixture : CoreTest<SeasonSearchService>
    {
        [Test]
        public void composite_season_command_targets_missing_version_slots()
        {
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.IsManagedSeries(5)).Returns(true);
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.SearchMissing(5, true, true, 2)).ReturnsAsync(0);
            Subject.Execute(new SeasonSearchCommand { SeriesId = 5, SeasonNumber = 2, Trigger = CommandTrigger.Manual });
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(5, true, true, 2), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.SeasonSearch(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void ordinary_season_command_keeps_existing_dispatch()
        {
            var empty = new List<DownloadDecision>();
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.SeasonSearch(5, 2, false, true, true, false)).ReturnsAsync(empty);
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(s => s.ProcessDecisions(empty)).ReturnsAsync(new ProcessedDecisions(empty, empty, empty));
            Subject.Execute(new SeasonSearchCommand { SeriesId = 5, SeasonNumber = 2, Trigger = CommandTrigger.Manual });
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.SeasonSearch(5, 2, false, true, true, false), Times.Once());
        }
    }
}
