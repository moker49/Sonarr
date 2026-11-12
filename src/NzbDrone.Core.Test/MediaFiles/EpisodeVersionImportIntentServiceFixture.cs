using System;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionImportIntentServiceFixture : DbTest<EpisodeVersionImportIntentService, EpisodeVersionImportIntent>
    {
        private Series _series;
        private SeriesVersion _version;
        private Episode _first;
        private Episode _second;
        private EpisodeDownloadReservation _reservation;
        private string _source;
        private string _destination;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 10).With(s => s.Path = @"C:\TV\Show".AsOsAgnostic()).BuildNew());
            var definition = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "HD", Priority = 0, IsPrimary = true });
            _version = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = definition.Id });
            _first = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
            _second = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
            _reservation = Db.Insert(new EpisodeDownloadReservation { SeriesId = _series.Id, SeriesVersionId = _version.Id, ReleaseKey = "pack", State = EpisodeDownloadReservationState.Submitted, DownloadClientId = 7, DownloadId = "job", NormalizedDownloadId = "JOB", CreatedAt = DateTime.UtcNow });
            Db.Insert(new EpisodeDownloadReservationItem { ReservationId = _reservation.Id, SeriesVersionId = _version.Id, EpisodeId = _first.Id });
            Db.Insert(new EpisodeDownloadReservationItem { ReservationId = _reservation.Id, SeriesVersionId = _version.Id, EpisodeId = _second.Id });
            _source = @"C:\Downloads\Show.mkv".AsOsAgnostic();
            _destination = @"C:\TV\Show\Show - HD.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_source)).Returns(true);
        }

        [Test]
        public void prepared_intent_survives_reads_and_locks_the_pack_until_cancelled()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "Job", new[] { _first.Id, _second.Id }, _source, _destination);

            Subject.GetPrepared(_series.Id).Should().ContainSingle(i => i.Id == id && i.State == EpisodeVersionImportIntentState.Prepared);
            Action overlap = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, @"C:\TV\Show\Other.mkv".AsOsAgnostic());
            overlap.Should().Throw<InvalidOperationException>();
            Subject.CancelPrepared(id);
            Subject.GetPrepared(_series.Id).Should().BeEmpty();
            Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination).Should().BePositive();
            AllStoredModels.Should().HaveCount(2);
        }

        [Test]
        public void a_pending_destination_cannot_be_claimed_for_another_episode()
        {
            Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            var otherSource = @"C:\Downloads\Other.mkv".AsOsAgnostic();
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(otherSource)).Returns(true);
            Action duplicate = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _second.Id }, otherSource, _destination);
            duplicate.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().ContainSingle();
        }

        [Test]
        public void a_pending_source_cannot_be_claimed_for_another_episode()
        {
            Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            Action duplicate = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _second.Id }, _source, @"C:\TV\Show\Other.mkv".AsOsAgnostic());
            duplicate.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().ContainSingle();
        }

        [Test]
        public void invalid_reservation_or_occupied_slot_never_creates_an_intent()
        {
            Action wrongClient = () => Subject.Prepare(_reservation.Id, 8, "job", new[] { _first.Id }, _source, _destination);
            wrongClient.Should().Throw<InvalidOperationException>();
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _version.Id, EpisodeId = _first.Id, VersionEpisodeFileId = 99 });
            Action occupied = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            occupied.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void rejects_destination_outside_series_or_already_present_on_disk()
        {
            Action outside = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, @"C:\TV\Other\Show.mkv".AsOsAgnostic());
            outside.Should().Throw<InvalidOperationException>();
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_destination)).Returns(true);
            Action exists = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            exists.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void existing_assignment_cannot_bypass_an_import_lock()
        {
            Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            var file = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new NzbDrone.Core.Qualities.QualityModel()).With(f => f.Languages = new System.Collections.Generic.List<NzbDrone.Core.Languages.Language>()).BuildNew());
            var assignments = Mocker.Resolve<VersionEpisodeFileAssignmentService>();

            Action assign = () => assignments.AssignUnoccupiedFile(_version.Id, file.Id, new[] { _first.Id });
            assign.Should().Throw<InvalidOperationException>();
        }
    }
}
