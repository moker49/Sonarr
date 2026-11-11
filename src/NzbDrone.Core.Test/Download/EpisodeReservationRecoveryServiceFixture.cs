using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class EpisodeReservationRecoveryServiceFixture : DbTest<EpisodeReservationRecoveryService, EpisodeDownloadReservation>
    {
        private Series _series;
        private SeriesVersion _version;
        private SeriesVersion _otherVersion;
        private Episode _episode;
        private Episode _otherEpisode;
        private EpisodeDownloadReservationService _reservations;
        private TrackedDownload _tracked;
        private int _reservationId;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 10).BuildNew());
            var first = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var second = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _version = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = first.Id });
            _otherVersion = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = second.Id });
            _episode = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
            _otherEpisode = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
            _tracked = new TrackedDownload
            {
                DownloadClient = 42,
                IsTrackable = true,
                DownloadItem = new DownloadClientItem { DownloadId = "Job-AbC", Title = "Series Episode", TotalSize = 1000 },
                RemoteEpisode = new RemoteEpisode { Series = _series, Episodes = new List<Episode> { _episode } }
            };
            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.GetTrackedDownloads()).Returns(() => new List<TrackedDownload> { _tracked });
            _reservations = Mocker.Resolve<EpisodeDownloadReservationService>();
            _reservationId = _reservations.Reserve(_version.Id, DownloadReleaseIdentity.GetClientKeys(_tracked.DownloadItem), new[] { _episode.Id });
            _reservations.BeginDispatch(_reservationId, 42);
            _reservations.MarkUncertain(_reservationId, "Client timed out");
        }

        [Test]
        public void binds_unique_matching_job_and_preserves_client_id_case()
        {
            Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "job-abc", "Verified in client");

            var row = _reservations.FindByClientDownload(42, "JOB-ABC");
            row.Id.Should().Be(_reservationId);
            row.DownloadId.Should().Be("Job-AbC");
            row.State.Should().Be(EpisodeDownloadReservationState.Submitted);
            _reservations.GetEpisodeIds(_reservationId).Should().Equal(_episode.Id);
            _tracked.EpisodeDownloadReservationId.Should().Be(_reservationId);
            _tracked.SeriesVersionId.Should().Be(_version.Id);
            _tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [Test]
        public void rejects_incomplete_pack_or_conflicting_version_without_changing_state()
        {
            _tracked.RemoteEpisode.Episodes.Add(_otherEpisode);
            Action extraEpisode = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            extraEpisode.Should().Throw<InvalidOperationException>();

            _tracked.RemoteEpisode.Episodes.Remove(_otherEpisode);
            _tracked.SeriesVersionId = _otherVersion.Id;
            Action wrongVersion = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            wrongVersion.Should().Throw<InvalidOperationException>();
            _reservations.ForSeries(_series.Id).Single().State.Should().Be(EpisodeDownloadReservationState.Uncertain);
        }

        [Test]
        public void rejects_multiple_matching_versions_instead_of_guessing()
        {
            var duplicate = _reservations.Reserve(_otherVersion.Id, DownloadReleaseIdentity.GetClientKeys(_tracked.DownloadItem), new[] { _episode.Id });
            _reservations.BeginDispatch(duplicate, 42);
            _reservations.MarkUncertain(duplicate, "Client timed out");

            Action bind = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            bind.Should().Throw<InvalidOperationException>();
            _reservations.ForSeries(_series.Id).Should().OnlyContain(r => r.State == EpisodeDownloadReservationState.Uncertain);
        }

        [Test]
        public void rejects_untrackable_and_changed_release_identity()
        {
            _tracked.IsTrackable = false;
            Action untrackable = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            untrackable.Should().Throw<InvalidOperationException>();

            _tracked.IsTrackable = true;
            _tracked.DownloadItem.TotalSize = 999;
            Action wrongRelease = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            wrongRelease.Should().Throw<InvalidOperationException>();
            _reservations.FindByClientDownload(42, "Job-AbC").Should().BeNull();
        }

        [Test]
        public void rejects_duplicate_tracked_jobs_and_jobs_in_unsafe_states()
        {
            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.GetTrackedDownloads()).Returns(new List<TrackedDownload> { _tracked, _tracked });
            Action duplicate = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            duplicate.Should().Throw<InvalidOperationException>();

            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.GetTrackedDownloads()).Returns(new List<TrackedDownload> { _tracked });
            _tracked.State = TrackedDownloadState.Importing;
            Action importing = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            importing.Should().Throw<InvalidOperationException>();
            _reservations.ForSeries(_series.Id).Single().State.Should().Be(EpisodeDownloadReservationState.Uncertain);
        }

        [Test]
        public void rejects_a_client_job_already_bound_to_another_reservation()
        {
            var other = _reservations.Reserve(_otherVersion.Id, "other-release", new[] { _otherEpisode.Id });
            _reservations.BeginDispatch(other, 42);
            _reservations.Submitted(other, "job-abc");

            Action bind = () => Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "Job-AbC", "Checked");
            bind.Should().Throw<InvalidOperationException>();
            _reservations.ForSeries(_series.Id).Single(r => r.Id == _reservationId).State.Should().Be(EpisodeDownloadReservationState.Uncertain);
        }

        [Test]
        public void reconciliation_waits_for_existing_import_check_before_binding()
        {
            using var checkEntered = new ManualResetEventSlim();
            using var allowCheckToFinish = new ManualResetEventSlim();
            using var recoveryStarted = new ManualResetEventSlim();
            _tracked.DownloadItem.Status = DownloadItemStatus.Completed;
            Mocker.GetMock<IProvideImportItemService>()
                .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                .Returns(() =>
                {
                    checkEntered.Set();
                    allowCheckToFinish.Wait(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Stop check after the synchronization point");
                });
            var completed = Mocker.Resolve<CompletedDownloadService>();
            var check = Task.Run(() => Assert.Throws<InvalidOperationException>(() => completed.Check(_tracked)));

            try
            {
                checkEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                var recovery = Task.Run(() =>
                {
                    recoveryStarted.Set();
                    Subject.BindTrackedDownload(_series.Id, _reservationId, 42, "job-abc", "Checked");
                });
                recoveryStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                recovery.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
                allowCheckToFinish.Set();
                check.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                recovery.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                _tracked.State.Should().Be(TrackedDownloadState.ImportBlocked);
            }
            finally
            {
                allowCheckToFinish.Set();
            }
        }
    }
}
