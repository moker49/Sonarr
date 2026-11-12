using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class VersionEpisodeSearchCommandFixture : CoreTest<EpisodeSearchService>
    {
        [SetUp]
        public void Setup()
        {
            var empty = new List<DownloadDecision>();
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(p => p.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .ReturnsAsync(new ProcessedDecisions(empty, empty, empty));
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), false))
                .ReturnsAsync(empty);
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.EpisodeSearch(It.IsAny<int>(), It.IsAny<bool>(), false, It.IsAny<int?>()))
                .ReturnsAsync(empty);
        }

        [Test]
        public void targeted_command_passes_version_to_search()
        {
            Subject.Execute(new EpisodeSearchCommand(new List<int> { 3 }) { SeriesVersionId = 7 });

            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, It.IsAny<bool>(), false, 7), Times.Once());
            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, It.IsAny<bool>(), false), Times.Never());
        }

        [Test]
        public void ordinary_command_uses_existing_search_path()
        {
            Subject.Execute(new EpisodeSearchCommand(new List<int> { 3 }));

            Mocker.GetMock<ISearchForReleases>().Verify(s => s.EpisodeSearch(3, It.IsAny<bool>(), false), Times.Once());
        }
    }
}
