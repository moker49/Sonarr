using System;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class EpisodeDownloadReservationServiceFixture : DbTest<EpisodeDownloadReservationService, EpisodeDownloadReservation>
    {
        private Series _series;
        private SeriesVersion _sdr;
        private SeriesVersion _hdr;
        private Episode _first;
        private Episode _second;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 10).BuildNew());
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _sdr = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = sdr.Id });
            _hdr = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = hdr.Id });
            _first = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
            _second = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
        }

        [Test]
        public void season_pack_blocks_overlapping_episode_in_same_version_but_not_other_version()
        {
            Subject.Reserve(_sdr.Id, "pack", new[] { _first.Id, _second.Id });
            Action overlap = () => Subject.Reserve(_sdr.Id, "episode", new[] { _first.Id });
            overlap.Should().Throw<InvalidOperationException>();
            Subject.Reserve(_hdr.Id, "episode", new[] { _first.Id }).Should().BePositive();
            AllStoredModels.Should().HaveCount(2);
            Mocker.Resolve<BasicRepository<EpisodeDownloadReservationItem>>().All().Should().HaveCount(3);
        }

        [Test]
        public void release_frees_all_episode_slots_atomically()
        {
            var reservation = Subject.Reserve(_sdr.Id, "pack", new[] { _first.Id, _second.Id });
            Subject.Release(reservation);
            AllStoredModels.Should().BeEmpty();
            Mocker.Resolve<BasicRepository<EpisodeDownloadReservationItem>>().All().Should().BeEmpty();
            Subject.Reserve(_sdr.Id, "episode", new[] { _first.Id }).Should().BePositive();
        }

        [Test]
        public void rejects_other_series_and_duplicate_release_without_partial_reservations()
        {
            var other = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id + 100).BuildNew());
            Action crossSeries = () => Subject.Reserve(_sdr.Id, "bad", new[] { _first.Id, other.Id });
            crossSeries.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();

            Subject.Reserve(_sdr.Id, "same-release", new[] { _first.Id });
            Action duplicate = () => Subject.Reserve(_sdr.Id, "same-release", new[] { _second.Id });
            duplicate.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().ContainSingle();
        }

        [Test]
        public void rejects_retired_or_unassigned_versions_and_duplicate_episodes()
        {
            _sdr.Retired = true;
            Mocker.Resolve<BasicRepository<SeriesVersion>>().Update(_sdr);
            Action retired = () => Subject.Reserve(_sdr.Id, "retired", new[] { _first.Id });
            retired.Should().Throw<InvalidOperationException>();

            _series.QualityProfileId = 99;
            Mocker.Resolve<BasicRepository<Series>>().Update(_series);
            Action unassigned = () => Subject.Reserve(_hdr.Id, "unassigned", new[] { _first.Id });
            Action duplicates = () => Subject.Reserve(_hdr.Id, "duplicate", new[] { _first.Id, _first.Id });
            unassigned.Should().Throw<InvalidOperationException>();
            duplicates.Should().Throw<ArgumentException>();
            AllStoredModels.Should().BeEmpty();
        }
    }
}
