using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionImportInspectionFixture : CoreTest<EpisodeVersionImportInspectionService>
    {
        private EpisodeVersionImportIntent _intent;

        [SetUp]
        public void Setup()
        {
            _intent = new EpisodeVersionImportIntent { Id = 1, SeriesId = 2, State = EpisodeVersionImportIntentState.Moving, SourcePath = "source", DestinationPath = "destination", SourceSize = 10, SourceHash = new string('A', 64), CopyOnly = false };
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetActive(2)).Returns(() => new List<EpisodeVersionImportIntent> { _intent });
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(s => s.Read(It.IsAny<string>())).Returns(new EpisodeImportFileFingerprint { Size = 10, Hash = new string('A', 64) });
        }

        [Test]
        public void identifies_unchanged_source_and_absent_destination()
        {
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists("source")).Returns(true);

            var result = Subject.Inspect(2, 1);

            result.Observation.Should().Be(EpisodeVersionImportObservation.SourceVerifiedDestinationAbsent);
            result.Source.Should().Be(EpisodeVersionImportFileObservation.Matches);
            result.Destination.Should().Be(EpisodeVersionImportFileObservation.Absent);
        }

        [TestCase(false, false, EpisodeVersionImportObservation.DestinationVerified)]
        [TestCase(false, true, EpisodeVersionImportObservation.NeedsReview)]
        [TestCase(true, true, EpisodeVersionImportObservation.DestinationVerified)]
        [TestCase(true, false, EpisodeVersionImportObservation.NeedsReview)]
        public void distinguishes_move_and_copy_observations(bool copyOnly, bool sourceExists, EpisodeVersionImportObservation expected)
        {
            _intent.CopyOnly = copyOnly;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists("source")).Returns(sourceExists);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists("destination")).Returns(true);

            Subject.Inspect(2, 1).Observation.Should().Be(expected);
        }

        [Test]
        public void mismatched_or_unreadable_files_require_review()
        {
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists("destination")).Returns(true);
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(s => s.Read("destination")).Returns(new EpisodeImportFileFingerprint { Size = 10, Hash = new string('B', 64) });
            Subject.Inspect(2, 1).Destination.Should().Be(EpisodeVersionImportFileObservation.Different);
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Setup(s => s.Read("destination")).Throws(new System.IO.IOException());

            var result = Subject.Inspect(2, 1);
            result.Observation.Should().Be(EpisodeVersionImportObservation.NeedsReview);
            result.Destination.Should().Be(EpisodeVersionImportFileObservation.Unavailable);
        }

        [Test]
        public void moved_state_with_only_the_source_is_treated_as_inconsistent()
        {
            _intent.State = EpisodeVersionImportIntentState.Moved;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists("source")).Returns(true);
            Subject.Inspect(2, 1).Observation.Should().Be(EpisodeVersionImportObservation.NeedsReview);
        }

        [Test]
        public void prepared_intents_report_presence_without_hashing()
        {
            _intent.State = EpisodeVersionImportIntentState.Prepared;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists("source")).Returns(true);
            Subject.Inspect(2, 1).Observation.Should().Be(EpisodeVersionImportObservation.Prepared);
            Mocker.GetMock<IEpisodeImportFileFingerprintService>().Verify(f => f.Read(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void refuses_an_import_from_another_series()
        {
            Mocker.GetMock<IEpisodeVersionImportIntentService>().Setup(s => s.GetActive(3)).Returns(new List<EpisodeVersionImportIntent>());
            Assert.Throws<InvalidOperationException>(() => Subject.Inspect(3, 1));
        }
    }
}
