using FluentAssertions;
using NUnit.Framework;
using Sonarr.Api.V3.Queue;

namespace NzbDrone.Api.Test.v3.Queue
{
    [TestFixture]
    public class QueueVersionResourceFixture
    {
        [Test]
        public void exposes_the_tracked_series_version()
        {
            var resource = new NzbDrone.Core.Queue.Queue { SeriesVersionId = 17 }.ToResource(false, false);
            resource.SeriesVersionId.Should().Be(17);
        }
    }
}
