using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class EpisodeVersionDestinationPlannerFixture : CoreTest<EpisodeVersionDestinationPlanner>
    {
        private LocalEpisode _local;
        private string _destination;
        private List<EpisodeFile> _files;

        [SetUp]
        public void Setup()
        {
            _local = new LocalEpisode
            {
                Series = new Series { Id = 1, Path = @"C:\TV\Example".AsOsAgnostic() },
                SeriesVersionId = 7,
                SeriesVersionLabel = "HD",
                Path = @"C:\Downloads\Example.mkv".AsOsAgnostic(),
                Episodes = new List<Episode> { new Episode { Id = 10, SeriesId = 1, EpisodeFileId = 5 } }
            };
            _destination = @"C:\TV\Example\Example - HD.mkv".AsOsAgnostic();
            _files = new List<EpisodeFile>();
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(1)).Returns(() => _files);
            Mocker.GetMock<IBuildFileNames>().Setup(s => s.BuildVersionFilePath(It.IsAny<List<Episode>>(), It.IsAny<Series>(), It.IsAny<EpisodeFile>(), ".mkv", "HD", null, It.IsAny<List<CustomFormat>>())).Returns(() => _destination);
        }

        [Test]
        public void plans_an_unoccupied_version_destination()
        {
            Subject.Plan(_local).Should().Be(_destination);
        }

        [Test]
        public void allows_the_existing_file_of_the_selected_version()
        {
            _files.Add(new EpisodeFile { Id = 5, SeriesId = 1, RelativePath = "Example - HD.mkv" });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(_destination)).Returns(true);
            Subject.Plan(_local).Should().Be(_destination);
        }

        [Test]
        public void rejects_another_registered_file_or_an_untracked_file_at_destination()
        {
            _files.Add(new EpisodeFile { Id = 6, SeriesId = 1, RelativePath = "Example - HD.mkv" });
            Assert.Throws<InvalidOperationException>(() => Subject.Plan(_local));
            _files.Clear();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(_destination)).Returns(true);
            Assert.Throws<InvalidOperationException>(() => Subject.Plan(_local));
        }

        [Test]
        public void rejects_destinations_outside_the_series_folder()
        {
            _destination = @"C:\TV\Other\Example.mkv".AsOsAgnostic();
            Assert.Throws<InvalidOperationException>(() => Subject.Plan(_local));
        }
    }
}
