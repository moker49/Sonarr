using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class EpisodeImportVersionContextFixture : CoreTest<EpisodeImportVersionContext>
    {
        private LocalEpisode _local;
        private DownloadClientItem _item;
        private TrackedDownload _tracked;
        private EpisodeDownloadReservation _reservation;

        [SetUp]
        public void Setup()
        {
            _local = new LocalEpisode { Series = new Series { Id = 1 }, Episodes = new List<Episode> { new Episode { Id = 5, SeriesId = 1 } } };
            _item = new DownloadClientItem { DownloadId = "job", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7 } };
            _tracked = new TrackedDownload { DownloadClient = 7, EpisodeDownloadReservationId = 12, SeriesVersionId = 34 };
            _reservation = new EpisodeDownloadReservation { Id = 12, SeriesId = 1, SeriesVersionId = 34, State = EpisodeDownloadReservationState.Submitted };
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.FindByClientDownload(7, "job")).Returns(() => _reservation);
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.GetEpisodeIds(12)).Returns(new List<int> { 5, 6 });
        }

        [Test]
        public void applies_isolated_child_profile_and_version_file_context()
        {
            var originalSeries = _local.Series;
            var originalEpisode = _local.Episodes[0];
            var versionSeries = new Series { Id = 1, QualityProfileId = 99 };
            var versionEpisode = new Episode { Id = 5, SeriesId = 1, EpisodeFileId = 77 };
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Setup(s => s.ForVersion(It.IsAny<RemoteEpisode>(), 34))
                .Returns(new RemoteEpisode { Series = versionSeries, Episodes = new List<Episode> { versionEpisode }, SeriesVersionId = 34, SeriesVersionLabel = "HD" });

            Subject.Apply(_local, _item, _tracked);

            _local.Series.Should().BeSameAs(versionSeries);
            _local.Episodes.Should().ContainSingle().Which.Should().BeSameAs(versionEpisode);
            _local.SeriesVersionId.Should().Be(34);
            _local.SeriesVersionLabel.Should().Be("HD");
            originalSeries.QualityProfileId.Should().NotBe(99);
            originalEpisode.EpisodeFileId.Should().Be(0);
        }

        [Test]
        public void rejects_episode_outside_the_reservation_without_mutating_context()
        {
            _local.Episodes.Add(new Episode { Id = 10, SeriesId = 1 });
            var originalSeries = _local.Series;

            Assert.Throws<InvalidOperationException>(() => Subject.Apply(_local, _item, _tracked));

            _local.Series.Should().BeSameAs(originalSeries);
            _local.SeriesVersionId.Should().BeNull();
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Verify(s => s.ForVersion(It.IsAny<RemoteEpisode>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void rejects_mismatched_or_unresolved_reservations()
        {
            _tracked.DownloadClient = 8;
            Assert.Throws<InvalidOperationException>(() => Subject.Apply(_local, _item, _tracked));
            _tracked.DownloadClient = 7;
            _reservation.State = EpisodeDownloadReservationState.Uncertain;
            Assert.Throws<InvalidOperationException>(() => Subject.Apply(_local, _item, _tracked));
            _reservation = null;
            Assert.Throws<InvalidOperationException>(() => Subject.Apply(_local, _item, _tracked));
        }

        [Test]
        public void ordinary_downloads_leave_episode_context_unchanged()
        {
            _reservation = null;
            var original = _local.Series;
            Subject.Apply(_local, _item, null);
            _local.Series.Should().BeSameAs(original);
            _local.SeriesVersionId.Should().BeNull();
        }
    }
}
