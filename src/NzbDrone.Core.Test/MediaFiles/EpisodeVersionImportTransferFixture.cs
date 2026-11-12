using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionImportTransferFixture : CoreTest<EpisodeVersionImportTransferService>
    {
        private EpisodeVersionImportIntent _intent;

        [SetUp]
        public void Setup()
        {
            _intent = new EpisodeVersionImportIntent { Id = 2, SeriesId = 1, SourcePath = @"C:\Downloads\Show.mkv".AsOsAgnostic(), DestinationPath = @"C:\TV\Show\Show - HD.mkv".AsOsAgnostic(), State = EpisodeVersionImportIntentState.Prepared };
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetActive(1)).Returns(new List<EpisodeVersionImportIntent> { _intent });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns(true);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void records_move_intent_before_transfer_and_confirms_afterwards(bool copyOnly)
        {
            var sequence = new MockSequence();
            Mocker.GetMock<IEpisodeVersionImportIntentService>().InSequence(sequence).Setup(s => s.BeginMove(2, copyOnly));
            if (copyOnly)
            {
                Mocker.GetMock<IDiskProvider>().InSequence(sequence).Setup(d => d.CopyFile(_intent.SourcePath, _intent.DestinationPath, false));
            }
            else
            {
                Mocker.GetMock<IDiskProvider>().InSequence(sequence).Setup(d => d.MoveFile(_intent.SourcePath, _intent.DestinationPath, false));
            }
            Mocker.GetMock<IEpisodeVersionImportIntentService>().InSequence(sequence).Setup(s => s.ConfirmMoved(2));

            Subject.Transfer(1, 2, copyOnly);

            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.BeginMove(2, copyOnly), Times.Once());
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.ConfirmMoved(2), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.CopyFile(_intent.SourcePath, _intent.DestinationPath, false), copyOnly ? Times.Once() : Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFile(_intent.SourcePath, _intent.DestinationPath, false), copyOnly ? Times.Never() : Times.Once());
        }

        [Test]
        public void partial_transfer_keeps_files_and_marks_the_journal_uncertain()
        {
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Throws(new IOException("Interrupted"));

            Assert.Throws<IOException>(() => Subject.Transfer(1, 2, false));

            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.MarkUncertain(2, It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.ConfirmMoved(2), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.DeleteFile(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void failed_preflight_does_not_transfer_or_mark_an_unstarted_move_uncertain()
        {
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.BeginMove(2, false)).Throws(new InvalidOperationException("Changed"));

            Assert.Throws<InvalidOperationException>(() => Subject.Transfer(1, 2, false));

            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.MarkUncertain(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
        }
    }
}
