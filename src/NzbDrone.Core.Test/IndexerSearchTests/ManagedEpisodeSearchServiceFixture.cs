using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class ManagedEpisodeSearchServiceFixture : CoreTest<ManagedEpisodeSearchService>
    {
        private List<WantedEpisodeVersion> _wanted;
        private List<NzbDrone.Core.Queue.Queue> _queue;
        private List<Episode> _episodes;
        private List<int?> _searchedVersions;

        [SetUp]
        public void Setup()
        {
            _wanted = new List<WantedEpisodeVersion>();
            _queue = new List<NzbDrone.Core.Queue.Queue>();
            _episodes = new List<Episode>
            {
                new Episode { Id = 1, SeriesId = 5, SeasonNumber = 1, EpisodeFileId = 99 },
                new Episode { Id = 2, SeriesId = 5, SeasonNumber = 1 },
                new Episode { Id = 3, SeriesId = 5, SeasonNumber = 2 }
            };
            _searchedVersions = new List<int?>();
            Mocker.GetMock<IManagedEpisodeWantedService>().Setup(w => w.Missing(It.IsAny<int?>(), It.IsAny<bool>(), true, It.IsAny<DateTime>())).Returns(() => _wanted);
            Mocker.GetMock<IQueueService>().Setup(q => q.GetQueue()).Returns(() => _queue);
            Mocker.GetMock<IEpisodeService>().Setup(e => e.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns((IEnumerable<int> ids) => _episodes.Where(e => ids.Contains(e.Id)).ToList());
            var empty = new List<DownloadDecision>();
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), false, It.IsAny<int?>()))
                .Callback<int, bool, bool, int?>((id, manual, interactive, version) => _searchedVersions.Add(version)).ReturnsAsync(empty);
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.SeasonSearch(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<List<Episode>>(), It.IsAny<bool>(), It.IsAny<bool>(), false, It.IsAny<int?>()))
                .Callback<int, int, List<Episode>, bool, bool, bool, int?>((series, season, episodes, monitored, manual, interactive, version) => _searchedVersions.Add(version)).ReturnsAsync(empty);
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(p => p.ProcessDecisions(It.IsAny<List<DownloadDecision>>())).ReturnsAsync(new ProcessedDecisions(empty, empty, empty));
        }

        private void Want(int episodeId, int versionId, int priority = 0)
        {
            _wanted.Add(new WantedEpisodeVersion { SeriesId = 5, EpisodeId = episodeId, SeasonNumber = episodeId == 3 ? 2 : 1, SeriesVersionId = versionId, Priority = priority });
        }

        [Test]
        public async Task counts_reports_grabbed_across_versions()
        {
            Want(1, 7);
            Want(1, 8, 1);
            var grabbed = new List<DownloadDecision> { new DownloadDecision(new NzbDrone.Core.Parser.Model.RemoteEpisode()) };
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(p => p.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .ReturnsAsync(new ProcessedDecisions(grabbed, new List<DownloadDecision>(), new List<DownloadDecision>()));
            (await Subject.SearchMissing(5, true, true)).Should().Be(2);
        }

        [Test]
        public async Task empty_selection_does_not_query_indexers()
        {
            (await Subject.SearchMissing(5, true, true)).Should().Be(0);
            _searchedVersions.Should().BeEmpty();
        }

        [Test]
        public async Task searches_secondary_slot_even_when_the_legacy_primary_has_a_file()
        {
            Want(1, 8);
            await Subject.SearchMissing(5, true, true);
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(1, true, false, 8), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public async Task groups_season_searches_by_version_in_priority_order()
        {
            Want(1, 8, 1);
            Want(2, 8, 1);
            Want(1, 7);
            Want(2, 7);
            await Subject.SearchMissing(null, true, false);
            _searchedVersions.Should().Equal(7, 8);
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.SeasonSearch(5, 1, It.Is<List<Episode>>(e => e.Count == 2), true, false, false, 7), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.SeasonSearch(5, 1, It.Is<List<Episode>>(e => e.Count == 2), true, false, false, 8), Times.Once());
        }

        [TestCase(7, 8)]
        [TestCase(8, 7)]
        public async Task queue_coverage_is_version_specific(int queuedVersion, int searchedVersion)
        {
            Want(1, 7);
            Want(1, 8, 1);
            _queue.Add(new NzbDrone.Core.Queue.Queue { Episode = _episodes[0], SeriesVersionId = queuedVersion });
            await Subject.SearchMissing(5, true, true);
            _searchedVersions.Should().Equal(searchedVersion);
        }

        [Test]
        public async Task ambiguous_legacy_queue_entry_blocks_both_versions()
        {
            Want(1, 7);
            Want(1, 8, 1);
            _queue.Add(new NzbDrone.Core.Queue.Queue { Episode = _episodes[0] });
            await Subject.SearchMissing(5, true, true);
            _searchedVersions.Should().BeEmpty();
        }

        [Test]
        public async Task season_selection_never_dispatches_other_seasons()
        {
            Want(1, 7);
            Want(3, 7);
            await Subject.SearchMissing(5, false, true, 2);
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, true, false, 7), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(1, It.IsAny<bool>(), false, It.IsAny<int?>()), Times.Never());
            Mocker.GetMock<IManagedEpisodeWantedService>().Verify(w => w.Missing(5, false, true, It.Is<DateTime>(d => d.Kind == DateTimeKind.Utc)), Times.Once());
        }

        [Test]
        public async Task rechecks_queue_before_each_version_dispatch()
        {
            Want(1, 7);
            Want(1, 8, 1);
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(1, true, false, 7)).ReturnsAsync(() =>
            {
                _queue.Add(new NzbDrone.Core.Queue.Queue { Episode = _episodes[0], SeriesVersionId = 8 });
                return new List<DownloadDecision>();
            });
            await Subject.SearchMissing(5, true, true);
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(1, true, false, 8), Times.Never());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void changed_episode_coverage_fails_before_search_or_grab(bool removed)
        {
            Want(1, 7);
            if (removed)
            {
                _episodes.Clear();
            }
            else
            {
                _episodes[0].SeriesId = 999;
            }
            Func<Task> search = () => Subject.SearchMissing(5, true, true);
            search.Should().ThrowAsync<InvalidOperationException>().GetAwaiter().GetResult();
            Mocker.GetMock<IProcessDownloadDecisions>().Verify(p => p.ProcessDecisions(It.IsAny<List<DownloadDecision>>()), Times.Never());
        }

        [Test]
        public void routes_using_the_current_profile_definition()
        {
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(5)).Returns(new Series { QualityProfileId = 10 });
            Mocker.GetMock<ICompositeProfileDraftService>().Setup(p => p.GetProfileIds()).Returns(new HashSet<int> { 10 });
            Subject.IsManagedSeries(5).Should().BeTrue();
        }
    }
}
