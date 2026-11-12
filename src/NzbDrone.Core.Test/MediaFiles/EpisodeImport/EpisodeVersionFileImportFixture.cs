using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class EpisodeVersionFileImportFixture : CoreTest<EpisodeVersionFileImportService>
    {
        private LocalEpisode _local;
        private DownloadClientItem _item;
        private string _destination;

        [SetUp]
        public void Setup()
        {
            var series = new Series { Id = 1, Path = @"C:\TV\Show".AsOsAgnostic(), QualityProfile = new QualityProfile() };
            var episode = new Episode { Id = 5, SeriesId = 1, SeasonNumber = 1 };
            _local = new LocalEpisode { Series = series, SeriesVersionId = 3, Episodes = new List<Episode> { episode }, Path = @"C:\Downloads\Show.mkv".AsOsAgnostic(), Quality = new QualityModel(), Size = 123 };
            _item = new DownloadClientItem { DownloadId = "job", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7 }, CanMoveFiles = false };
            _destination = @"C:\TV\Show\Show - HD.mkv".AsOsAgnostic();
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.FindByClientDownload(7, "job")).Returns(new EpisodeDownloadReservation { Id = 2, SeriesId = 1, SeriesVersionId = 3, State = EpisodeDownloadReservationState.Submitted });
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeVersionImportIntent>());
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(new List<Episode> { episode });
            Mocker.GetMock<IMakeImportDecision>().Setup(s => s.GetDecision(_local, _item)).Returns(() => new ImportDecision(_local));
            Mocker.GetMock<IEpisodeVersionDestinationPlanner>().Setup(s => s.Plan(_local)).Returns(_destination);
            Mocker.GetMock<IRootFolderService>().Setup(s => s.GetBestRootFolderPath(series.Path)).Returns(@"C:\TV".AsOsAgnostic());
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileSize(_local.Path)).Returns(123);
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.Prepare(2, 7, "job", It.IsAny<IReadOnlyCollection<int>>(), _local.Path, _destination)).Returns(10);
            Mocker.GetMock<IEpisodeVersionImportWorkflow>().Setup(s => s.Resume(1, 10, true, It.IsAny<EpisodeFile>())).Returns(new EpisodeFile { Id = 4 });
        }

        [Test]
        public void prepares_validated_empty_slot_import_and_respects_copy_only_client()
        {
            Subject.Import(_local, true, _item, ImportMode.Auto).Id.Should().Be(4);
            Mocker.GetMock<IEpisodeImportVersionContext>().Verify(s => s.Apply(_local, _item, It.IsAny<TrackedDownload>()), Times.Once());
            Mocker.GetMock<IMakeImportDecision>().Verify(s => s.GetDecision(_local, _item), Times.Once());
            Mocker.GetMock<IEpisodeVersionImportWorkflow>().Verify(s => s.Resume(1, 10, true, It.Is<EpisodeFile>(f => f.SeriesId == 1 && f.Size == 123 && f.RelativePath == "Show - HD.mkv")), Times.Once());
        }

        [Test]
        public void completed_retry_uses_existing_intent_without_reading_or_transferring_source_again()
        {
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeVersionImportIntent> { new EpisodeVersionImportIntent { Id = 10, SeriesVersionId = 3, ReservationId = 2, SourcePath = _local.Path, DestinationPath = _destination, State = EpisodeVersionImportIntentState.Completed } });
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetEpisodeIds(10)).Returns(new List<int> { 5 });

            Subject.Import(_local, true, _item, ImportMode.Auto).Id.Should().Be(4);

            Mocker.GetMock<IDiskProvider>().Verify(s => s.GetFileSize(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IEpisodeVersionImportWorkflow>().Verify(s => s.Resume(1, 10, true, null), Times.Once());
        }

        [Test]
        public void rejected_final_decision_never_prepares_an_intent()
        {
            Mocker.GetMock<IMakeImportDecision>().Setup(s => s.GetDecision(_local, _item)).Returns(new ImportDecision(_local, new ImportRejection(ImportRejectionReason.NotQualityUpgrade, "Rejected")));
            Assert.Throws<InvalidOperationException>(() => Subject.Import(_local, true, _item, ImportMode.Auto));
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.Prepare(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void prepared_retry_revalidates_instead_of_reusing_an_old_import_decision()
        {
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetForSeries(1)).Returns(new List<EpisodeVersionImportIntent> { new EpisodeVersionImportIntent { Id = 9, SeriesVersionId = 3, ReservationId = 2, SourcePath = _local.Path, DestinationPath = _destination, State = EpisodeVersionImportIntentState.Prepared } });
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetEpisodeIds(9)).Returns(new List<int> { 5 });

            Subject.Import(_local, true, _item, ImportMode.Auto).Id.Should().Be(4);

            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.CancelPrepared(9), Times.Once());
            Mocker.GetMock<IMakeImportDecision>().Verify(s => s.GetDecision(_local, _item), Times.Once());
        }

        [Test]
        public void scripts_and_existing_file_registration_remain_blocked()
        {
            Assert.Throws<InvalidOperationException>(() => Subject.Import(_local, false, _item, ImportMode.Auto));
            Mocker.GetMock<IConfigService>().SetupGet(c => c.UseScriptImport).Returns(true);
            Assert.Throws<InvalidOperationException>(() => Subject.Import(_local, true, _item, ImportMode.Auto));
        }
    }
}
