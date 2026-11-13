using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class SeriesSearchServiceFixture : CoreTest<SeriesSearchService>
    {
        private Series _series;

        [SetUp]
        public void Setup()
        {
            _series = new Series
                      {
                          Id = 1,
                          Title = "Title",
                          Seasons = new List<Season>()
                      };

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(_series);

            Mocker.GetMock<ISearchForReleases>()
                  .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), false, false, true, false))
                  .Returns(Task.FromResult(new List<DownloadDecision>()));

            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns(Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>())));
        }

        [Test]
        public void composite_series_search_preserves_monitored_season_selection()
        {
            _series.Seasons = new List<Season>
            {
                new Season { SeasonNumber = 1, Monitored = false },
                new Season { SeasonNumber = 2, Monitored = true }
            };
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.IsManagedSeries(_series.Id)).Returns(true);
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.SearchMissing(_series.Id, true, true, 2)).ReturnsAsync(0);

            Subject.Execute(new SeriesSearchCommand(_series.Id) { Trigger = CommandTrigger.Manual });

            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(_series.Id, true, true, 2), Times.Once());
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(_series.Id, true, true, 1), Times.Never());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.SeasonSearch(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void composite_series_without_monitored_seasons_searches_monitored_episode_slots()
        {
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.IsManagedSeries(_series.Id)).Returns(true);
            Mocker.GetMock<IManagedEpisodeSearchService>().Setup(s => s.SearchMissing(_series.Id, true, false, null)).ReturnsAsync(0);
            Subject.Execute(new SeriesSearchCommand(_series.Id));
            Mocker.GetMock<IManagedEpisodeSearchService>().Verify(s => s.SearchMissing(_series.Id, true, false, null), Times.Once());
        }

        [Test]
        public void should_only_include_monitored_seasons()
        {
            _series.Seasons = new List<Season>
                              {
                                  new Season { SeasonNumber = 0, Monitored = false },
                                  new Season { SeasonNumber = 1, Monitored = true }
                              };

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(v => v.SeasonSearch(_series.Id, It.IsAny<int>(), false, true, true, false), Times.Exactly(_series.Seasons.Count(s => s.Monitored)));
        }

        [Test]
        public void should_start_with_lower_seasons_first()
        {
            var seasonOrder = new List<int>();

            _series.Seasons = new List<Season>
                              {
                                  new Season { SeasonNumber = 3, Monitored = true },
                                  new Season { SeasonNumber = 1, Monitored = true },
                                  new Season { SeasonNumber = 2, Monitored = true }
                              };

            Mocker.GetMock<ISearchForReleases>()
                  .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), false, true, true, false))
                  .Returns(Task.FromResult(new List<DownloadDecision>()))
                  .Callback<int, int, bool, bool, bool, bool>((seriesId, seasonNumber, missingOnly, monitoredOnly, userInvokedSearch, interactiveSearch) => seasonOrder.Add(seasonNumber));

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            seasonOrder.First().Should().Be(_series.Seasons.OrderBy(s => s.SeasonNumber).First().SeasonNumber);
        }
    }
}
