using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class RenameEpisodeFileServiceFixture : CoreTest<RenameEpisodeFileService>
    {
        private Series _series;
        private List<EpisodeFile> _episodeFiles;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew()
                                     .Build();

            _episodeFiles = Builder<EpisodeFile>.CreateListOfSize(2)
                                                .All()
                                                .With(e => e.SeriesId = _series.Id)
                                                .With(e => e.SeasonNumber = 1)
                                                .Build()
                                                .ToList();

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(_series.Id))
                  .Returns(_series);

            Mocker.GetMock<IVersionEpisodeFileAssignmentService>().Setup(s => s.GetOwnedFileIds(_series.Id)).Returns(new HashSet<int>());
        }

        private void GivenNoEpisodeFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Get(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<EpisodeFile>());
        }

        private void GivenEpisodeFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Get(It.IsAny<IEnumerable<int>>()))
                  .Returns(_episodeFiles);
        }

        private void GivenMovedFiles()
        {
            Mocker.GetMock<IMoveEpisodeFiles>()
                  .Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), _series));
        }

        [Test]
        public void excludes_version_owned_files_from_rename_previews()
        {
            var versionFile = _episodeFiles[0];
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(_series.Id)).Returns(new List<EpisodeFile> { versionFile });
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodeBySeries(_series.Id)).Returns(new List<Episode> { new Episode { EpisodeFileId = versionFile.Id } });
            Mocker.GetMock<IVersionEpisodeFileAssignmentService>().Setup(s => s.GetOwnedFileIds(_series.Id)).Returns(new HashSet<int> { versionFile.Id });

            var previews = Subject.GetRenamePreviews(_series.Id);

            Assert.That(previews, Is.Empty);
            Mocker.GetMock<IBuildFileNames>().Verify(s => s.BuildFilePath(It.IsAny<List<Episode>>(), It.IsAny<Series>(), It.IsAny<EpisodeFile>(), It.IsAny<string>(), It.IsAny<NamingConfig>(), It.IsAny<List<NzbDrone.Core.CustomFormats.CustomFormat>>()), Times.Never());
        }

        [Test]
        public void skips_version_owned_files_while_renaming_ordinary_files()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();
            var versionFile = _episodeFiles[0];
            var ordinaryFile = _episodeFiles[1];
            Mocker.GetMock<IVersionEpisodeFileAssignmentService>().Setup(s => s.IsOwnedFile(versionFile.Id)).Returns(true);

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { versionFile.Id, ordinaryFile.Id }));

            Mocker.GetMock<IMoveEpisodeFiles>().Verify(s => s.MoveEpisodeFile(versionFile, _series), Times.Never());
            Mocker.GetMock<IMoveEpisodeFiles>().Verify(s => s.MoveEpisodeFile(ordinaryFile, _series), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(versionFile), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_publish_event_if_no_files_to_rename()
        {
            GivenNoEpisodeFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_not_publish_event_if_no_files_are_renamed()
        {
            GivenEpisodeFiles();

            Mocker.GetMock<IMoveEpisodeFiles>()
                  .Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<Series>()))
                  .Throws(new SameFilenameException("Same file name", "Filename"));

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_publish_event_if_files_are_renamed()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Once());
        }

        [Test]
        public void should_update_moved_files()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Update(It.IsAny<EpisodeFile>()), Times.Exactly(2));
        }

        [Test]
        public void should_get_episodefiles_by_ids_only()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            var files = new List<int> { 1 };

            Subject.Execute(new RenameFilesCommand(_series.Id, files));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Get(files), Times.Once());
        }
    }
}
