using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class AddFixture : CoreTest<PendingReleaseService>
    {
        private DownloadDecision _temporarilyRejected;
        private Series _series;
        private Episode _episode;
        private QualityProfile _profile;
        private ReleaseInfo _release;
        private ParsedEpisodeInfo _parsedEpisodeInfo;
        private RemoteEpisode _remoteEpisode;
        private List<PendingRelease> _heldReleases;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew()
                                     .Build();

            _episode = Builder<Episode>.CreateNew()
                                       .Build();

            _profile = new QualityProfile
                       {
                           Name = "Test",
                           Cutoff = Quality.HDTV720p.Id,
                           Items = new List<QualityProfileQualityItem>
                                   {
                                       new QualityProfileQualityItem { Allowed = true, Quality = Quality.HDTV720p },
                                       new QualityProfileQualityItem { Allowed = true, Quality = Quality.WEBDL720p },
                                       new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray720p }
                                   },
                       };

            _series.QualityProfile = new LazyLoaded<QualityProfile>(_profile);

            _release = Builder<ReleaseInfo>.CreateNew().Build();

            _parsedEpisodeInfo = Builder<ParsedEpisodeInfo>.CreateNew().Build();
            _parsedEpisodeInfo.Quality = new QualityModel(Quality.HDTV720p);

            _remoteEpisode = new RemoteEpisode();
            _remoteEpisode.Episodes = new List<Episode> { _episode };
            _remoteEpisode.Series = _series;
            _remoteEpisode.ParsedEpisodeInfo = _parsedEpisodeInfo;
            _remoteEpisode.Release = _release;

            _temporarilyRejected = new DownloadDecision(_remoteEpisode, new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Temp Rejected", RejectionType.Temporary));

            _heldReleases = new List<PendingRelease>();

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(s => s.All())
                  .Returns(_heldReleases);

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(s => s.AllBySeriesId(It.IsAny<int>()))
                  .Returns<int>(i => _heldReleases.Where(v => v.SeriesId == i).ToList());

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(_series);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Series> { _series });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetEpisodes(It.IsAny<ParsedEpisodeInfo>(), _series, true, null))
                  .Returns(new List<Episode> { _episode });

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                  .Setup(s => s.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns((List<DownloadDecision> d) => d);
        }

        private void GivenHeldRelease(string title, string indexer, DateTime publishDate, PendingReleaseReason reason = PendingReleaseReason.Delay)
        {
            var release = _release.JsonClone();
            release.Indexer = indexer;
            release.PublishDate = publishDate;

            var heldReleases = Builder<PendingRelease>.CreateListOfSize(1)
                                                   .All()
                                                   .With(h => h.SeriesId = _series.Id)
                                                   .With(h => h.SeriesVersionId = null)
                                                   .With(h => h.Title = title)
                                                   .With(h => h.Release = release)
                                                   .With(h => h.Reason = reason)
                                                   .With(h => h.ParsedEpisodeInfo = _parsedEpisodeInfo)
                                                   .Build();

            _heldReleases.AddRange(heldReleases);
        }

        [Test]
        public void should_add()
        {
            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        [Test]
        public void should_not_add_if_it_is_the_same_release_from_the_same_indexer()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyNoInsert();
        }

        [Test]
        public void sibling_versions_keep_separate_pending_copies()
        {
            _series.SeriesType = SeriesTypes.Standard;
            _parsedEpisodeInfo.AirDate = null;
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate);
            _heldReleases[0].SeriesVersionId = 1;
            _remoteEpisode.SeriesVersionId = 2;
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<Series>()))
                .Returns(new RemoteEpisode { Series = _series, Episodes = new List<Episode> { _episode } });
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Setup(s => s.ForVersion(It.IsAny<RemoteEpisode>(), 1))
                .Returns<RemoteEpisode, int>((remote, version) => remote);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            Mocker.GetMock<IPendingReleaseRepository>().Verify(v => v.Insert(It.Is<PendingRelease>(p => p.SeriesVersionId == 2)), Times.Once());
        }

        [Test]
        public void unresolved_version_is_preserved_without_episode_matches()
        {
            _series.SeriesType = SeriesTypes.Standard;
            _parsedEpisodeInfo.AirDate = null;
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate);
            _heldReleases[0].SeriesVersionId = 1;
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<Series>()))
                .Returns(new RemoteEpisode { Series = _series, Episodes = new List<Episode> { _episode } });

            Subject.GetPendingRemoteEpisodes(_series.Id).Single().Episodes.Should().BeEmpty();
            Mocker.GetMock<IPendingReleaseRepository>().Verify(v => v.Delete(It.IsAny<PendingRelease>()), Times.Never());
        }

        [Test]
        public void unresolved_version_does_not_create_a_duplicate_pending_release()
        {
            _series.SeriesType = SeriesTypes.Standard;
            _parsedEpisodeInfo.AirDate = null;
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate);
            _heldReleases[0].SeriesVersionId = 1;
            _remoteEpisode.SeriesVersionId = 1;
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<Series>()))
                .Returns(new RemoteEpisode { Series = _series, Episodes = new List<Episode> { _episode } });

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyNoInsert();
        }

        [Test]
        public void oldest_pending_release_does_not_cross_version_delay_windows()
        {
            _series.SeriesType = SeriesTypes.Standard;
            _parsedEpisodeInfo.AirDate = null;
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedEpisodeInfo>(), _series))
                .Returns(() => new RemoteEpisode { Series = _series, Episodes = new List<Episode> { _episode } });
            GivenHeldRelease(_release.Title, _release.Indexer, DateTime.UtcNow.AddDays(-2));
            _heldReleases[0].SeriesVersionId = 7;
            GivenHeldRelease(_release.Title, _release.Indexer, DateTime.UtcNow.AddHours(-1));
            _heldReleases[1].SeriesVersionId = 8;
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Setup(c => c.ForVersion(It.IsAny<RemoteEpisode>(), It.IsAny<int>()))
                .Returns((RemoteEpisode source, int version) => source);

            var oldest = Subject.OldestPendingRelease(_series.Id, new[] { _episode.Id }, 8);
            oldest.SeriesVersionId.Should().Be(8);
            oldest.Release.PublishDate.Should().Be(_heldReleases[1].Release.PublishDate);
            Subject.OldestPendingRelease(_series.Id, new[] { _episode.Id }).Should().BeNull();
        }

        [Test]
        public void versioned_pending_preserves_its_target_when_reprocessed()
        {
            Mocker.GetMock<NzbDrone.Core.Indexers.IIndexerStatusService>().Setup(s => s.GetBlockedProviders()).Returns(new List<NzbDrone.Core.Indexers.IndexerStatus>());
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate);
            _heldReleases[0].SeriesVersionId = 1;

            Subject.GetPending().Should().ContainSingle().Which.PendingSeriesVersionId.Should().Be(1);
            Mocker.GetMock<IPendingReleaseRepository>().Verify(v => v.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_not_add_if_it_is_the_same_release_from_the_same_indexer_twice()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.DownloadClientUnavailable);
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.Fallback);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyNoInsert();
        }

        [Test]
        public void should_remove_duplicate_if_it_is_the_same_release_from_the_same_indexer_twice()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.DownloadClientUnavailable);
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate, PendingReleaseReason.Fallback);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Fallback);

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Verify(v => v.Delete(It.IsAny<int>()), Times.Once());
        }

        [Test]
        public void should_add_if_title_is_different()
        {
            GivenHeldRelease(_release.Title + "-RP", _release.Indexer, _release.PublishDate);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        [Test]
        public void should_add_if_indexer_is_different()
        {
            GivenHeldRelease(_release.Title, "AnotherIndexer", _release.PublishDate);

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        [Test]
        public void should_add_if_publish_date_is_different()
        {
            GivenHeldRelease(_release.Title, _release.Indexer, _release.PublishDate.AddHours(1));

            Subject.Add(_temporarilyRejected, PendingReleaseReason.Delay);

            VerifyInsert();
        }

        private void VerifyInsert()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                .Verify(v => v.Insert(It.IsAny<PendingRelease>()), Times.Once());
        }

        private void VerifyNoInsert()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                .Verify(v => v.Insert(It.IsAny<PendingRelease>()), Times.Never());
        }
    }
}
