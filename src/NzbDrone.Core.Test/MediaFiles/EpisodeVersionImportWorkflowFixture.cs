using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionImportWorkflowFixture : CoreTest<EpisodeVersionImportWorkflow>
    {
        private EpisodeVersionImportIntent _intent;
        private EpisodeFile _metadata;

        [SetUp]
        public void Setup()
        {
            _intent = new EpisodeVersionImportIntent { Id = 2, SeriesId = 1, SeriesVersionId = 3, SourceSize = 123, State = EpisodeVersionImportIntentState.Prepared };
            _metadata = new EpisodeFile { SeriesId = 1, Size = 123, Quality = new QualityModel(), Languages = new List<NzbDrone.Core.Languages.Language>(), RelativePath = "Show.mkv" };
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetForSeries(1)).Returns(() => new List<EpisodeVersionImportIntent> { _intent });
            Mocker.GetMock<IEpisodeVersionImportCompletionService>().Setup(s => s.Complete(1, 2)).Callback(() => _intent.State = EpisodeVersionImportIntentState.Completed).Returns(4);
            Mocker.GetMock<IEpisodeVersionImportTransferService>().Setup(s => s.Transfer(1, 2, false)).Callback(() => { _intent.State = EpisodeVersionImportIntentState.Moved; _intent.CopyOnly = false; });
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.RegisterMovedFile(1, 2, It.IsAny<EpisodeFile>())).Callback(() => { _intent.State = EpisodeVersionImportIntentState.Registered; _intent.RegisteredFileId = 4; }).Returns(4);
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetEpisodeIds(2)).Returns(new List<int> { 5 });
            Mocker.GetMock<IEpisodeVersionResolver>().Setup(s => s.ResolveMany(It.IsAny<IReadOnlyCollection<int>>())).Returns(new Dictionary<int, List<ResolvedEpisodeVersion>> { [5] = new List<ResolvedEpisodeVersion> { new ResolvedEpisodeVersion { EpisodeId = 5, SeriesId = 1, SeriesVersionId = 3, EpisodeFileId = 4 } } });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.Get(4)).Returns(new EpisodeFile { Id = 4, SeriesId = 1, Size = 123 });
        }

        [Test]
        public void transfers_and_registers_once_then_reuses_the_registered_file_on_retry()
        {
            Subject.Resume(1, 2, false, _metadata).Id.Should().Be(4);
            Subject.Resume(1, 2, false, null).Id.Should().Be(4);

            Mocker.GetMock<IEpisodeVersionImportTransferService>().Verify(s => s.Transfer(1, 2, false), Times.Once());
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.RegisterMovedFile(1, 2, It.IsAny<EpisodeFile>()), Times.Once());
            Mocker.GetMock<IEpisodeVersionImportCompletionService>().Verify(s => s.Complete(1, 2), Times.Once());
        }

        [Test]
        public void moved_import_resumes_registration_without_repeating_transfer()
        {
            _intent.State = EpisodeVersionImportIntentState.Moved;
            _intent.CopyOnly = false;

            Subject.Resume(1, 2, false, _metadata).Id.Should().Be(4);

            Mocker.GetMock<IEpisodeVersionImportTransferService>().Verify(s => s.Transfer(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void registration_failure_can_be_retried_without_moving_the_file_again()
        {
            var attempts = 0;
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.RegisterMovedFile(1, 2, It.IsAny<EpisodeFile>())).Returns(() =>
            {
                if (++attempts == 1)
                {
                    throw new InvalidOperationException("Registration interrupted");
                }

                _intent.State = EpisodeVersionImportIntentState.Registered;
                _intent.RegisteredFileId = 4;
                return 4;
            });

            Assert.Throws<InvalidOperationException>(() => Subject.Resume(1, 2, false, _metadata));
            Subject.Resume(1, 2, false, _metadata).Id.Should().Be(4);

            Mocker.GetMock<IEpisodeVersionImportTransferService>().Verify(s => s.Transfer(1, 2, false), Times.Once());
        }

        [Test]
        public void resume_cannot_change_the_recorded_transfer_mode()
        {
            _intent.State = EpisodeVersionImportIntentState.Moved;
            _intent.CopyOnly = true;
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(1, 2, false, _metadata));
        }

        [TestCase(EpisodeVersionImportIntentState.Moving)]
        [TestCase(EpisodeVersionImportIntentState.Uncertain)]
        public void interrupted_transfer_requires_reconciliation(EpisodeVersionImportIntentState state)
        {
            _intent.State = state;
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(1, 2, false, _metadata));
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Verify(s => s.RegisterMovedFile(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<EpisodeFile>()), Times.Never());
        }

        [Test]
        public void registered_retry_rejects_changed_episode_ownership()
        {
            _intent.State = EpisodeVersionImportIntentState.Registered;
            _intent.RegisteredFileId = 4;
            Mocker.GetMock<IEpisodeVersionResolver>().Setup(s => s.ResolveMany(It.IsAny<IReadOnlyCollection<int>>())).Returns(new Dictionary<int, List<ResolvedEpisodeVersion>> { [5] = new List<ResolvedEpisodeVersion> { new ResolvedEpisodeVersion { SeriesId = 1, SeriesVersionId = 3, EpisodeFileId = 99 } } });

            Assert.Throws<InvalidOperationException>(() => Subject.Resume(1, 2, false, null));
        }
    }
}
