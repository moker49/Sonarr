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
        private int PrepareMovedPack()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id, _second.Id }, _source, _destination);
            Subject.BeginMove(id, false);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_source)).Returns(false);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_destination)).Returns(true);
            Subject.ConfirmMoved(id);
            return id;
        }

        private EpisodeFile NewImportFile()
        {
            return new EpisodeFile { SeriesId = _series.Id, SeasonNumber = _first.SeasonNumber, RelativePath = "Show - HD.mkv", Size = 123, DateAdded = DateTime.UtcNow, Quality = new NzbDrone.Core.Qualities.QualityModel(), Languages = new System.Collections.Generic.List<NzbDrone.Core.Languages.Language>() };
        }

        [Test]
        public void completion_writes_history_once_and_preserves_episode_coverage_after_unlocking()
        {
            var definition = Mocker.Resolve<BasicRepository<CompositeProfileVersion>>().Get(_version.CompositeProfileVersionId);
            definition.IsPrimary = false;
            Mocker.Resolve<BasicRepository<CompositeProfileVersion>>().Update(definition);
            var id = PrepareMovedPack();
            var fileId = Subject.RegisterMovedFile(_series.Id, id, NewImportFile());
            var completion = Mocker.Resolve<EpisodeVersionImportCompletionService>();

            completion.Complete(_series.Id, id).Should().Be(fileId);
            completion.Complete(_series.Id, id).Should().Be(fileId);

            Subject.GetActive(_series.Id).Should().BeEmpty();
            Subject.GetForSeries(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Completed);
            Subject.GetEpisodeIds(id).Should().HaveCount(2);
            Mocker.Resolve<BasicRepository<NzbDrone.Core.History.EpisodeHistory>>().All().Should().HaveCount(2).And.OnlyContain(h => h.SeriesVersionId == _version.Id && h.DownloadId == "job");
            Mocker.Resolve<BasicRepository<NzbDrone.Core.Download.History.DownloadHistory>>().All().Should().ContainSingle(h => h.EventType == NzbDrone.Core.Download.History.DownloadHistoryEventType.FileImported);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            Dapper.SqlMapper.ExecuteScalar<int>(connection, "SELECT COUNT(*) FROM \"EpisodeVersionImportEpisodeLocks\"").Should().Be(0);
            Dapper.SqlMapper.ExecuteScalar<int>(connection, "SELECT COUNT(*) FROM \"EpisodeVersionImportPathLocks\"").Should().Be(0);
            Dapper.SqlMapper.ExecuteScalar<int>(connection, "SELECT COUNT(*) FROM \"EpisodeVersionImportCompletions\" WHERE \"PublishedAt\" IS NULL").Should().Be(1);
        }

        [Test]
        public void changed_assignment_blocks_completion_without_history_or_lock_release()
        {
            var definition = Mocker.Resolve<BasicRepository<CompositeProfileVersion>>().Get(_version.CompositeProfileVersionId);
            definition.IsPrimary = false;
            Mocker.Resolve<BasicRepository<CompositeProfileVersion>>().Update(definition);
            var id = PrepareMovedPack();
            Subject.RegisterMovedFile(_series.Id, id, NewImportFile());
            var assignments = Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>();
            var assignment = System.Linq.Enumerable.First(assignments.All());
            assignment.VersionEpisodeFileId = null;
            assignments.Update(assignment);

            Action complete = () => Mocker.Resolve<EpisodeVersionImportCompletionService>().Complete(_series.Id, id);

            complete.Should().Throw<InvalidOperationException>();
            Mocker.Resolve<BasicRepository<NzbDrone.Core.History.EpisodeHistory>>().All().Should().BeEmpty();
            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Registered);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            Dapper.SqlMapper.ExecuteScalar<int>(connection, "SELECT COUNT(*) FROM \"EpisodeVersionImportPathLocks\"").Should().Be(2);
        }

        [Test]
        public void completion_record_failure_rolls_back_history_and_preserves_locks()
        {
            var definition = Mocker.Resolve<BasicRepository<CompositeProfileVersion>>().Get(_version.CompositeProfileVersionId);
            definition.IsPrimary = false;
            Mocker.Resolve<BasicRepository<CompositeProfileVersion>>().Update(definition);
            var id = PrepareMovedPack();
            Subject.RegisterMovedFile(_series.Id, id, NewImportFile());
            using (var connection = Mocker.Resolve<IMainDatabase>().OpenConnection())
            {
                Dapper.SqlMapper.Execute(connection, "INSERT INTO \"EpisodeVersionImportCompletions\" (\"ImportId\", \"CreatedAt\") VALUES (@id, @now)", new { id, now = DateTime.UtcNow });
            }

            Action complete = () => Mocker.Resolve<EpisodeVersionImportCompletionService>().Complete(_series.Id, id);

            complete.Should().Throw<Exception>();
            Mocker.Resolve<BasicRepository<NzbDrone.Core.History.EpisodeHistory>>().All().Should().BeEmpty();
            Mocker.Resolve<BasicRepository<NzbDrone.Core.Download.History.DownloadHistory>>().All().Should().BeEmpty();
            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Registered);
            using var check = Mocker.Resolve<IMainDatabase>().OpenConnection();
            Dapper.SqlMapper.ExecuteScalar<int>(check, "SELECT COUNT(*) FROM \"EpisodeVersionImportEpisodeLocks\"").Should().Be(2);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void registers_shared_file_and_version_assignments_atomically(bool primary)
        {
            _first.EpisodeFileId = primary ? 0 : 222;
            _second.EpisodeFileId = primary ? 0 : 333;
            Mocker.Resolve<BasicRepository<Episode>>().Update(_first);
            Mocker.Resolve<BasicRepository<Episode>>().Update(_second);
            var definitions = Mocker.Resolve<BasicRepository<CompositeProfileVersion>>();
            var definition = definitions.Get(_version.CompositeProfileVersionId);
            definition.IsPrimary = primary;
            definitions.Update(definition);
            var id = PrepareMovedPack();

            var fileId = Subject.RegisterMovedFile(_series.Id, id, NewImportFile());

            Mocker.Resolve<BasicRepository<VersionEpisodeFile>>().All().Should().ContainSingle(f => f.EpisodeFileId == fileId && f.SeriesVersionId == _version.Id);
            Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>().All().Should().HaveCount(2);
            Mocker.Resolve<BasicRepository<Episode>>().Get(_first.Id).EpisodeFileId.Should().Be(primary ? fileId : 222);
            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Registered && i.RegisteredFileId == fileId);
            Subject.GetEpisodeIds(id).Should().HaveCount(2);
        }

        [Test]
        public void occupied_primary_link_rolls_back_file_and_all_version_assignments()
        {
            var id = PrepareMovedPack();
            var file = NewImportFile();

            Action register = () => Subject.RegisterMovedFile(_series.Id, id, file);

            register.Should().Throw<InvalidOperationException>();
            file.Id.Should().Be(0);
            Mocker.Resolve<BasicRepository<EpisodeFile>>().All().Should().BeEmpty();
            Mocker.Resolve<BasicRepository<VersionEpisodeFile>>().All().Should().BeEmpty();
            Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>().All().Should().BeEmpty();
            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Moved);
        }

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
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(f => f.Read(It.IsAny<string>())).Returns(new EpisodeImportFileFingerprint { Size = 123, Hash = new string('A', 64) });
        }

        [Test]
        public void move_evidence_persists_until_a_matching_move_is_confirmed()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            Subject.BeginMove(id, false);

            var moving = Subject.GetActive(_series.Id).Should().ContainSingle().Which;
            moving.State.Should().Be(EpisodeVersionImportIntentState.Moving);
            moving.SourceSize.Should().Be(123);
            moving.SourceHash.Should().Be(new string('A', 64));
            Action cancel = () => Subject.CancelPrepared(id);
            cancel.Should().Throw<InvalidOperationException>();

            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_source)).Returns(false);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_destination)).Returns(true);
            Subject.ConfirmMoved(id);

            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Moved);
            Action overlap = () => Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, @"C:\TV\Show\Other.mkv".AsOsAgnostic());
            overlap.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void mismatched_destination_cannot_be_confirmed_and_locks_are_retained()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            Subject.BeginMove(id, false);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_source)).Returns(false);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_destination)).Returns(true);
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(f => f.Read(_destination)).Returns(new EpisodeImportFileFingerprint { Size = 123, Hash = new string('B', 64) });

            Action confirm = () => Subject.ConfirmMoved(id);
            confirm.Should().Throw<InvalidOperationException>();
            Subject.MarkUncertain(id, "Destination does not match source evidence");

            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Uncertain);
            Action cancel = () => Subject.CancelPrepared(id);
            cancel.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void copy_confirmation_requires_the_original_source_to_remain_unchanged()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            Subject.BeginMove(id, true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_destination)).Returns(true);
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(f => f.Read(_source)).Returns(new EpisodeImportFileFingerprint { Size = 123, Hash = new string('B', 64) });

            Action confirm = () => Subject.ConfirmMoved(id);
            confirm.Should().Throw<InvalidOperationException>();
            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Moving);
        }

        [Test]
        public void uncertain_move_can_be_recovered_only_with_matching_evidence_and_series()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            Subject.BeginMove(id, false);
            Subject.MarkUncertain(id, "Interrupted");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_source)).Returns(false);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(_destination)).Returns(true);

            Action wrongSeries = () => Subject.RecoverMoved(_series.Id + 100, id, "Checked");
            wrongSeries.Should().Throw<InvalidOperationException>();
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(f => f.Read(_destination)).Returns(new EpisodeImportFileFingerprint { Size = 123, Hash = new string('B', 64) });
            Action wrongContent = () => Subject.RecoverMoved(_series.Id, id, "Checked");
            wrongContent.Should().Throw<InvalidOperationException>();
            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Uncertain);

            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(f => f.Read(_destination)).Returns(new EpisodeImportFileFingerprint { Size = 123, Hash = new string('A', 64) });
            Subject.RecoverMoved(_series.Id, id, "Verified destination after interruption");

            Subject.GetActive(_series.Id).Should().ContainSingle(i => i.State == EpisodeVersionImportIntentState.Moved && i.Note == "Verified destination after interruption");
            Subject.GetEpisodeIds(id).Should().Equal(_first.Id);
            Action cancel = () => Subject.CancelPrepared(id);
            cancel.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void refuses_to_start_if_an_episode_became_occupied_after_preparation()
        {
            var id = Subject.Prepare(_reservation.Id, 7, "job", new[] { _first.Id }, _source, _destination);
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _version.Id, EpisodeId = _first.Id, VersionEpisodeFileId = 99 });

            Action begin = () => Subject.BeginMove(id, false);

            begin.Should().Throw<InvalidOperationException>();
            Subject.GetPrepared(_series.Id).Should().ContainSingle(i => i.Id == id);
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
