using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Tv;
using Sonarr.Api.V3.Series;

namespace NzbDrone.Api.Test.v3.Series
{
    [TestFixture]
    public class SeriesProfileConversionControllerFixture
    {
        [Test]
        public void preview_and_apply_preserve_route_identity_and_snapshot_token()
        {
            var planner = new Mock<ISeriesVersionAssignmentPlanner>();
            var assignments = new Mock<ISeriesVersionAssignmentService>();
            planner.Setup(p => p.Preview(1, 10, 3)).Returns(new SeriesVersionAssignmentPlan { SeriesId = 1, SnapshotToken = "snapshot" });
            assignments.Setup(a => a.Apply(1, 10, 3, "snapshot")).Returns(new List<SeriesVersion> { new SeriesVersion { Id = 5, SeriesId = 1 } });
            var controller = new SeriesProfileConversionController(planner.Object, assignments.Object);
            var request = new SeriesProfileConversionResource { CompositeProfileId = 10, ExistingFileVersionId = 3 };

            request.SnapshotToken = controller.Preview(1, request).SnapshotToken;
            controller.Apply(1, request).Should().ContainSingle(v => v.SeriesId == 1);
            assignments.Verify(a => a.Apply(1, 10, 3, "snapshot"), Times.Once());
        }

        [Test]
        public void stale_previews_return_conflict_and_missing_body_is_rejected()
        {
            var assignments = new Mock<ISeriesVersionAssignmentService>();
            assignments.Setup(a => a.Apply(1, 10, null, "old")).Throws(new InvalidOperationException("Stale preview"));
            var controller = new SeriesProfileConversionController(Mock.Of<ISeriesVersionAssignmentPlanner>(), assignments.Object);
            Assert.Throws<NzbDroneClientException>(() => controller.Apply(1, new SeriesProfileConversionResource { CompositeProfileId = 10, SnapshotToken = "old" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            Assert.Throws<NzbDroneClientException>(() => controller.Preview(1, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
