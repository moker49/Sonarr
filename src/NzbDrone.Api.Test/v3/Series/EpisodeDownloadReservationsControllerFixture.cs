using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Tv;
using Sonarr.Api.V3.Series;

namespace NzbDrone.Api.Test.v3.Series
{
    [TestFixture]
    public class EpisodeDownloadReservationsControllerFixture
    {
        [Test]
        public void lists_only_requested_series_with_episode_coverage()
        {
            var reservations = new Mock<IEpisodeDownloadReservationService>();
            reservations.Setup(s => s.ForSeries(1)).Returns(new List<EpisodeDownloadReservation> { new EpisodeDownloadReservation { Id = 2, SeriesId = 1, SeriesVersionId = 3 } });
            reservations.Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 4, 5 });
            var controller = new EpisodeDownloadReservationsController(Mock.Of<ISeriesService>(), reservations.Object, Mock.Of<IEpisodeReservationRecoveryService>());

            var response = controller.Get(1);
            response.Should().ContainSingle();
            response[0].EpisodeIds.Should().Equal(4, 5);
        }

        [Test]
        public void binding_reports_conflict_when_tracking_does_not_match()
        {
            var recovery = new Mock<IEpisodeReservationRecoveryService>();
            recovery.Setup(s => s.BindTrackedDownload(1, 2, 3, "job", "checked")).Throws(new InvalidOperationException("Ambiguous"));
            var controller = new EpisodeDownloadReservationsController(Mock.Of<ISeriesService>(), Mock.Of<IEpisodeDownloadReservationService>(), recovery.Object);

            var error = Assert.Throws<NzbDroneClientException>(() => controller.Bind(1, 2, new EpisodeReservationBindResource { DownloadClientId = 3, DownloadId = "job", Note = "checked" }));
            error.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Test]
        public void binding_requires_a_request_body()
        {
            var controller = new EpisodeDownloadReservationsController(Mock.Of<ISeriesService>(), Mock.Of<IEpisodeDownloadReservationService>(), Mock.Of<IEpisodeReservationRecoveryService>());
            var error = Assert.Throws<NzbDroneClientException>(() => controller.Bind(1, 2, null));
            error.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void successful_binding_returns_the_updated_reservation()
        {
            var recovery = new Mock<IEpisodeReservationRecoveryService>();
            var reservations = new Mock<IEpisodeDownloadReservationService>();
            reservations.Setup(s => s.ForSeries(1)).Returns(new List<EpisodeDownloadReservation> { new EpisodeDownloadReservation { Id = 2, SeriesId = 1, State = EpisodeDownloadReservationState.Submitted, DownloadId = "job" } });
            reservations.Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 4 });
            var controller = new EpisodeDownloadReservationsController(Mock.Of<ISeriesService>(), reservations.Object, recovery.Object);

            var result = controller.Bind(1, 2, new EpisodeReservationBindResource { DownloadClientId = 3, DownloadId = "job", Note = "checked" });

            result.State.Should().Be(EpisodeDownloadReservationState.Submitted);
            result.EpisodeIds.Should().Equal(4);
            recovery.Verify(s => s.BindTrackedDownload(1, 2, 3, "job", "checked"), Times.Once());
        }
    }
}
