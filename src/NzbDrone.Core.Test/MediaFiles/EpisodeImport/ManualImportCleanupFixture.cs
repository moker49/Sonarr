using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ManualImportCleanupFixture : CoreTest<ManualImportService>
    {
        [Test]
        public void missing_tracked_download_is_not_treated_as_an_untracked_import()
        {
            var series = new Series { Id = 1, Path = @"C:\TV\Example".AsOsAgnostic(), QualityProfile = new QualityProfile() };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(new List<Episode> { new Episode { Id = 2, SeriesId = 1 } });

            Subject.Execute(new ManualImportCommand { Files = new List<ManualImportFile> { new ManualImportFile { Path = @"C:\Downloads\Example\Example.S01E01.mkv".AsOsAgnostic(), SeriesId = 1, EpisodeIds = new List<int> { 2 }, DownloadId = "missing", Quality = new QualityModel() } } });

            Mocker.GetMock<IImportApprovedEpisodes>().Verify(s => s.Import(It.IsAny<List<ImportDecision>>(), It.IsAny<bool>(), It.IsAny<DownloadClientItem>(), It.IsAny<ImportMode>()), Times.Never());
        }

        [Test]
        public void blocked_tracked_import_does_not_delete_the_download_folder()
        {
            var series = new Series { Id = 1, Path = @"C:\TV\Example".AsOsAgnostic(), QualityProfile = new QualityProfile() };
            var episode = new Episode { Id = 2, SeriesId = 1 };
            var item = new DownloadClientItem { DownloadId = "job", CanMoveFiles = true, DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7 }, OutputPath = new OsPath(@"C:\Downloads\Example".AsOsAgnostic()) };
            var tracked = new TrackedDownload { DownloadClient = 7, DownloadItem = item, ImportItem = item };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(new List<Episode> { episode });
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find("job")).Returns(tracked);
            Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>())).Returns<LocalEpisode, DownloadClientItem>((local, download) => local);
            Mocker.GetMock<IImportApprovedEpisodes>().Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), true, item, It.IsAny<ImportMode>()))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, newDownload, download, mode) => new List<ImportResult> { new ImportResult(decisions[0], "Blocked") });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(It.IsAny<string>())).Returns(true);

            Subject.Execute(new ManualImportCommand { Files = new List<ManualImportFile> { new ManualImportFile { Path = @"C:\Downloads\Example\Example.S01E01.mkv".AsOsAgnostic(), SeriesId = 1, EpisodeIds = new List<int> { 2 }, DownloadId = "job", Quality = new QualityModel() } } });

            Mocker.GetMock<IDiskProvider>().Verify(s => s.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IDownloadedEpisodesImportService>().Verify(s => s.ShouldDeleteFolder(It.IsAny<System.IO.DirectoryInfo>(), It.IsAny<Series>()), Times.Never());
        }
    }
}
