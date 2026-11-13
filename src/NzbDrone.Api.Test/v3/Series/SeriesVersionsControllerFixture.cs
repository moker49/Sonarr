using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Tv;
using Sonarr.Api.V3.Series;

namespace NzbDrone.Api.Test.v3.Series
{
    [TestFixture]
    public class SeriesVersionsControllerFixture
    {
        [Test]
        public void returns_the_requested_series_status()
        {
            var statuses = new Mock<ISeriesVersionStatusService>();
            var expected = new List<SeriesVersionStatus> { new SeriesVersionStatus { Id = 7, Label = "HDR" } };
            statuses.Setup(s => s.Get(42)).Returns(expected);
            new SeriesVersionsController(statuses.Object).Get(42).Should().BeSameAs(expected);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void inconsistent_status_is_a_conflict(bool invalidDefinitions)
        {
            var statuses = new Mock<ISeriesVersionStatusService>();
            statuses.Setup(s => s.Get(42)).Throws(invalidDefinitions ? (Exception)new ValidationException("Invalid definitions") : new InvalidOperationException("Invalid ownership"));
            Action get = () => new SeriesVersionsController(statuses.Object).Get(42);
            get.Should().Throw<NzbDroneClientException>().Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
    }
}
