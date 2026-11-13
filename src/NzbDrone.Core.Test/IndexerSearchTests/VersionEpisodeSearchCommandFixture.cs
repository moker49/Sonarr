using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class VersionEpisodeSearchCommandFixture : CoreTest<EpisodeSearchService>
    {
        [SetUp]
        public void Setup()
        {
            var empty = new List<DownloadDecision>();
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(p => p.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .ReturnsAsync(new ProcessedDecisions(empty, empty, empty));
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), false))
                .ReturnsAsync(empty);
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), false, It.IsAny<int?>()))
                .ReturnsAsync(empty);
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(It.IsAny<Episode>(), It.IsAny<bool>(), false)).ReturnsAsync(empty);
            Mocker.GetMock<IQueueService>().Setup(q => q.GetQueue()).Returns(new List<NzbDrone.Core.Queue.Queue>());
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.SearchMissing(It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int?>())).ReturnsAsync(0);
        }

        [Test]
        public void targeted_command_passes_version_to_search()
        {
            Subject.Execute(new EpisodeSearchCommand(new List<int> { 3 }) { SeriesVersionId = 7 });

            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, It.IsAny<bool>(), false, 7), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, It.IsAny<bool>(), false), Times.Never());
        }

        [Test]
        public void ordinary_command_uses_existing_search_path()
        {
            Subject.Execute(new EpisodeSearchCommand(new List<int> { 3 }));

            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, It.IsAny<bool>(), false), Times.Once());
        }

        [Test]
        public void managed_missing_command_uses_version_selection_instead_of_legacy_file_links()
        {
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.IsManagedSeries(5)).Returns(true);
            Subject.Execute(new MissingEpisodeSearchCommand(5) { Monitored = false, Trigger = CommandTrigger.Manual });
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(5, false, true, null), Times.Once());
            Mocker.GetMock<IEpisodeService>().Verify(s => s.GetEpisodeBySeries(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void global_missing_search_separates_ordinary_and_composite_dispatch()
        {
            var ordinary = new Episode { Id = 1, SeriesId = 5 };
            var managed = new Episode { Id = 2, SeriesId = 6 };
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.IsManagedSeries(6)).Returns(true);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.EpisodesWithoutFiles(It.IsAny<PagingSpec<Episode>>()))
                .Returns(new PagingSpec<Episode> { Records = new List<Episode> { ordinary, managed } });

            Subject.Execute(new MissingEpisodeSearchCommand { Trigger = CommandTrigger.Manual });

            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(ordinary, true, false), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(managed, It.IsAny<bool>(), false), Times.Never());
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(null, true, true, null), Times.Once());
        }

        [Test]
        public void ordinary_series_missing_search_keeps_its_existing_path()
        {
            var ordinary = new Episode { Id = 1, SeriesId = 5, Monitored = true, AirDateUtc = DateTime.UtcNow.AddDays(-1) };
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(5)).Returns(new List<Episode> { ordinary });
            Subject.Execute(new MissingEpisodeSearchCommand(5) { Trigger = CommandTrigger.Manual });
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(ordinary, true, false), Times.Once());
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public void cutoff_search_does_not_submit_managed_replacements_through_the_legacy_path()
        {
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.IsManagedSeries(5)).Returns(true);
            Mocker.GetMock<IEpisodeCutoffService>().Setup(s => s.EpisodesWhereCutoffUnmet(It.IsAny<PagingSpec<Episode>>()))
                .Returns(new PagingSpec<Episode> { Records = new List<Episode> { new Episode { Id = 1, SeriesId = 5 } } });
            Subject.Execute(new CutoffUnmetEpisodeSearchCommand());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(It.IsAny<Episode>(), It.IsAny<bool>(), false), Times.Never());
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int?>()), Times.Never());
        }
    }
}
