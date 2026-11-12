using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;
using Sonarr.Api.V3.Series;

namespace NzbDrone.Api.Test.v3.Series
{
    [TestFixture]
    public class EpisodeVersionImportsControllerFixture
    {
        [Test]
        public void lists_active_imports_without_inspecting_files()
        {
            var intents = new Mock<IEpisodeVersionImportIntentService>();
            var inspection = new Mock<IEpisodeVersionImportInspectionService>();
            intents.Setup(s => s.GetActive(1)).Returns(new List<EpisodeVersionImportIntent> { new EpisodeVersionImportIntent { Id = 2, SeriesId = 1, SeriesVersionId = 3, State = EpisodeVersionImportIntentState.Moving } });
            intents.Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 4, 5 });
            var controller = new EpisodeVersionImportsController(Mock.Of<ISeriesService>(), intents.Object, inspection.Object);

            var result = controller.Get(1);

            result.Should().ContainSingle().Which.EpisodeIds.Should().Equal(4, 5);
            inspection.Verify(s => s.Inspect(It.IsAny<int>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void inspection_returns_the_observation_and_import_identity()
        {
            var intents = new Mock<IEpisodeVersionImportIntentService>();
            var inspection = new Mock<IEpisodeVersionImportInspectionService>();
            intents.Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 4 });
            inspection.Setup(s => s.Inspect(1, 2)).Returns(new EpisodeVersionImportInspection
            {
                Intent = new EpisodeVersionImportIntent { Id = 2, SeriesId = 1 },
                Observation = EpisodeVersionImportObservation.NeedsReview,
                Source = EpisodeVersionImportFileObservation.Absent,
                Destination = EpisodeVersionImportFileObservation.Different
            });
            var controller = new EpisodeVersionImportsController(Mock.Of<ISeriesService>(), intents.Object, inspection.Object);

            var result = controller.Inspect(1, 2);

            result.Import.EpisodeIds.Should().Equal(4);
            result.Observation.Should().Be(EpisodeVersionImportObservation.NeedsReview);
            result.Destination.Should().Be(EpisodeVersionImportFileObservation.Different);
        }
    }
}
