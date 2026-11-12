using System;
using System.Linq;
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
        public void empty_slot_dispatch_rechecks_version_assignments_and_primary_links_in_transaction()
        {
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _hdr.Id, EpisodeId = _first.Id, VersionEpisodeFileId = 99 });
            Action secondaryOccupied = () => Subject.ReserveEmptySlots(_hdr.Id, new[] { "release" }, new[] { _first.Id });
            secondaryOccupied.Should().Throw<InvalidOperationException>();
            Action primaryOccupied = () => Subject.ReserveEmptySlots(_sdr.Id, new[] { "release" }, new[] { _first.Id });
            primaryOccupied.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();

            _first.EpisodeFileId = 0;
            Mocker.Resolve<BasicRepository<Episode>>().Update(_first);
            Subject.ReserveEmptySlots(_sdr.Id, new[] { "release" }, new[] { _first.Id }).Should().BePositive();
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

        [Test]
        public void submitted_download_retains_slots_and_restores_client_identity()
        {
            var reservation = Subject.Reserve(_sdr.Id, "release", new[] { _first.Id, _second.Id });
            Subject.BeginDispatch(reservation, 42);
            Subject.Submitted(reservation, "client-job-1");

            var restored = Subject.FindByClientDownload(42, "Client-Job-1");
            restored.Id.Should().Be(reservation);
            restored.DownloadId.Should().Be("client-job-1");
            restored.State.Should().Be(EpisodeDownloadReservationState.Submitted);
            Subject.GetEpisodeIds(reservation).Should().Equal(new[] { _first.Id, _second.Id }.OrderBy(id => id));
            Subject.ForSeries(_series.Id).Should().ContainSingle();
            Action release = () => Subject.Release(reservation);
            release.Should().Throw<InvalidOperationException>();
            Mocker.Resolve<BasicRepository<EpisodeDownloadReservationItem>>().All().Should().HaveCount(2);
        }

        [Test]
        public void interrupted_dispatch_becomes_uncertain_on_restart_without_freeing_slots()
        {
            var reservation = Subject.Reserve(_sdr.Id, "release", new[] { _first.Id });
            Subject.BeginDispatch(reservation, 42);
            Subject.Handle(null);

            var recovered = Subject.ForSeries(_series.Id)[0];
            recovered.State.Should().Be(EpisodeDownloadReservationState.Uncertain);
            recovered.DownloadClientId.Should().Be(42);
            recovered.Note.Should().NotBeNullOrWhiteSpace();
            Action release = () => Subject.Release(reservation);
            release.Should().Throw<InvalidOperationException>();
            Action overlap = () => Subject.Reserve(_sdr.Id, "other", new[] { _first.Id });
            overlap.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void client_identity_cannot_be_bound_to_two_reservations()
        {
            var first = Subject.Reserve(_sdr.Id, "one", new[] { _first.Id });
            var second = Subject.Reserve(_hdr.Id, "two", new[] { _first.Id });
            Subject.BeginDispatch(first, 42);
            Subject.Submitted(first, "job");
            Subject.BeginDispatch(second, 42);

            Action duplicate = () => Subject.Submitted(second, "JOB");
            duplicate.Should().Throw<Exception>();
            Subject.ForSeries(_series.Id).Find(r => r.Id == second).State.Should().Be(EpisodeDownloadReservationState.Dispatching);
        }

        [Test]
        public void alternate_release_keys_detect_conflicts_and_expose_ambiguous_recovery_candidates()
        {
            var first = Subject.Reserve(_sdr.Id, new[] { "guid:first", "title:shared" }, new[] { _first.Id });
            Action aliasConflict = () => Subject.Reserve(_sdr.Id, new[] { "guid:other", "title:shared" }, new[] { _second.Id });
            aliasConflict.Should().Throw<InvalidOperationException>();
            var otherVersion = Subject.Reserve(_hdr.Id, new[] { "guid:second", "title:shared" }, new[] { _first.Id });
            Subject.BeginDispatch(first, 42);
            Subject.BeginDispatch(otherVersion, 42);
            Subject.Handle(null);

            Subject.FindUnboundCandidates(42, new[] { "title:shared" }).Should().HaveCount(2);
            Subject.FindUnboundCandidates(43, new[] { "title:shared" }).Should().BeEmpty();
            Subject.FindUnboundCandidates(42, new[] { "guid:first" }).Should().ContainSingle(r => r.Id == first);
        }

        [Test]
        public void releasing_prepared_reservation_removes_all_identity_keys()
        {
            var reservation = Subject.Reserve(_sdr.Id, new[] { "guid:first", "title:shared" }, new[] { _first.Id });
            Subject.Release(reservation);
            Mocker.Resolve<BasicRepository<EpisodeDownloadReservationKey>>().All().Should().BeEmpty();
            Subject.Reserve(_sdr.Id, new[] { "title:shared" }, new[] { _second.Id }).Should().BePositive();
        }

        [Test]
        public void rejects_out_of_order_transitions_and_preserves_uncertain_reservation()
        {
            var reservation = Subject.Reserve(_sdr.Id, "release", new[] { _first.Id });
            Action submitBeforeDispatch = () => Subject.Submitted(reservation, "job");
            submitBeforeDispatch.Should().Throw<InvalidOperationException>();

            Subject.BeginDispatch(reservation, 42);
            Subject.MarkUncertain(reservation, "The client did not confirm receipt.");
            Action submitAfterUncertain = () => Subject.Submitted(reservation, "job");
            Action dispatchAgain = () => Subject.BeginDispatch(reservation, 42);
            submitAfterUncertain.Should().Throw<InvalidOperationException>();
            dispatchAgain.Should().Throw<InvalidOperationException>();
            Subject.ForSeries(_series.Id)[0].State.Should().Be(EpisodeDownloadReservationState.Uncertain);
            Subject.GetEpisodeIds(reservation).Should().ContainSingle().Which.Should().Be(_first.Id);
        }

        [Test]
        public void definitive_rejection_keeps_history_but_allows_retry()
        {
            var reservation = Subject.Reserve(_sdr.Id, new[] { "guid:one", "title:one" }, new[] { _first.Id });
            Subject.BeginDispatch(reservation, 42);
            Subject.MarkDefinitivelyNotSubmitted(reservation, "Client explicitly rejected the job");

            var history = Subject.ForSeries(_series.Id).Single();
            history.State.Should().Be(EpisodeDownloadReservationState.Failed);
            history.Note.Should().Be("Client explicitly rejected the job");
            Subject.GetEpisodeIds(reservation).Should().Equal(_first.Id);
            Mocker.Resolve<BasicRepository<EpisodeDownloadReservationItem>>().All().Should().BeEmpty();
            Subject.HasUnboundForClient(42).Should().BeFalse();
            Subject.Reserve(_sdr.Id, new[] { "guid:one", "title:one" }, new[] { _first.Id }).Should().BePositive();
        }

        [Test]
        public void uncertainty_cannot_be_reclassified_as_definitive_failure()
        {
            var reservation = Subject.Reserve(_sdr.Id, "release", new[] { _first.Id });
            Subject.BeginDispatch(reservation, 42);
            Subject.MarkUncertain(reservation, "Timed out");

            Action fail = () => Subject.MarkDefinitivelyNotSubmitted(reservation, "Claimed rejection");
            fail.Should().Throw<InvalidOperationException>();
            Subject.GetEpisodeIds(reservation).Should().ContainSingle();
            Subject.HasUnboundForClient(42).Should().BeTrue();
        }
    }
}
