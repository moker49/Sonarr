using System;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OrganizerTests
{
    [TestFixture]

    public class BuildFilePathFixture : CoreTest<FileNameBuilder>
    {
        private NamingConfig _namingConfig;

        [SetUp]
        public void Setup()
        {
            _namingConfig = NamingConfig.Default;

            Mocker.GetMock<INamingConfigService>()
                  .Setup(c => c.GetConfig()).Returns(_namingConfig);
        }

        [Test]
        public void version_paths_keep_episode_naming_and_distinguish_sibling_versions()
        {
            var episodes = Builder<Episode>.CreateListOfSize(1).All().With(e => e.Title = "Episode Title").With(e => e.SeasonNumber = 1).With(e => e.EpisodeNumber = 5).Build().ToList();
            var series = Builder<Series>.CreateNew().With(s => s.Title = "30 Rock").With(s => s.Path = @"C:\Test\30 Rock".AsOsAgnostic()).With(s => s.SeasonFolder = false).With(s => s.SeriesType = SeriesTypes.Standard).Build();
            var file = Builder<EpisodeFile>.CreateNew().With(f => f.SceneName = "30 Rock - S01E05 - Episode Title").Build();

            var first = Subject.BuildVersionFilePath(episodes, series, file, ".mkv", "HD");
            var second = Subject.BuildVersionFilePath(episodes, series, file, ".mkv", "4K");

            first.Should().Be(@"C:\Test\30 Rock\30 Rock - S01E05 - Episode Title - HD.mkv".AsOsAgnostic());
            second.Should().Be(@"C:\Test\30 Rock\30 Rock - S01E05 - Episode Title - 4K.mkv".AsOsAgnostic());
        }

        [TestCase("../escape")]
        [TestCase(" label ")]
        [TestCase("")]
        public void version_path_rejects_unsafe_labels(string label)
        {
            Action build = () => Subject.BuildVersionFilePath(new System.Collections.Generic.List<Episode>(), new Series(), new EpisodeFile(), ".mkv", label);
            build.Should().Throw<ArgumentException>();
        }

        [Test]
        public void version_suffix_reserves_space_when_truncating_episode_titles()
        {
            _namingConfig.RenameEpisodes = true;
            _namingConfig.StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title}";
            var episodes = Builder<Episode>.CreateListOfSize(1).All().With(e => e.Title = new string('a', 240)).With(e => e.SeasonNumber = 1).With(e => e.EpisodeNumber = 5).Build().ToList();
            var series = Builder<Series>.CreateNew().With(s => s.Title = "Show").With(s => s.Path = @"C:\Test\Show".AsOsAgnostic()).With(s => s.SeasonFolder = false).With(s => s.SeriesType = SeriesTypes.Standard).Build();
            var file = new EpisodeFile { Quality = new QualityModel(Quality.HDTV720p) };
            Mocker.GetMock<IQualityDefinitionService>().Setup(s => s.Get(It.IsAny<Quality>()))
                .Returns<Quality>(quality => Quality.DefaultQualityDefinitions.First(d => d.Quality == quality));

            var path = Subject.BuildVersionFilePath(episodes, series, file, ".mkv", "Ultra HD");

            Path.GetFileName(path).GetByteCount().Should().BeLessOrEqualTo(LongPathSupport.MaxFileNameLength);
            path.Should().EndWith(" - Ultra HD.mkv");
        }

        [Test]
        [TestCase("30 Rock - S01E05 - Episode Title", 1, true, "Season {season:00}", @"C:\Test\30 Rock\Season 01\30 Rock - S01E05 - Episode Title.mkv")]
        [TestCase("30 Rock - S01E05 - Episode Title", 1, true, "Season {season}", @"C:\Test\30 Rock\Season 1\30 Rock - S01E05 - Episode Title.mkv")]
        [TestCase("30 Rock - S01E05 - Episode Title", 1, false, "Season {season:00}", @"C:\Test\30 Rock\30 Rock - S01E05 - Episode Title.mkv")]
        [TestCase("30 Rock - S01E05 - Episode Title", 1, false, "Season {season}", @"C:\Test\30 Rock\30 Rock - S01E05 - Episode Title.mkv")]
        [TestCase("30 Rock - S01E05 - Episode Title", 1, true, "ReallyUglySeasonFolder {season}", @"C:\Test\30 Rock\ReallyUglySeasonFolder 1\30 Rock - S01E05 - Episode Title.mkv")]
        [TestCase("30 Rock - S00E05 - Episode Title", 0, true, "Season {season}", @"C:\Test\30 Rock\MySpecials\30 Rock - S00E05 - Episode Title.mkv")]
        public void CalculateFilePath_SeasonFolder_SingleNumber(string filename, int seasonNumber, bool useSeasonFolder, string seasonFolderFormat, string expectedPath)
        {
            var fakeEpisodes = Builder<Episode>.CreateListOfSize(1)
                .All()
                .With(s => s.Title = "Episode Title")
                .With(s => s.SeasonNumber = seasonNumber)
                .With(s => s.EpisodeNumber = 5)
                .Build().ToList();
            var fakeSeries = Builder<Series>.CreateNew()
                .With(s => s.Title = "30 Rock")
                .With(s => s.Path = @"C:\Test\30 Rock".AsOsAgnostic())
                .With(s => s.SeasonFolder = useSeasonFolder)
                .With(s => s.SeriesType = SeriesTypes.Standard)
                .Build();
            var fakeEpisodeFile = Builder<EpisodeFile>.CreateNew()
                .With(s => s.SceneName = filename)
                .Build();

            _namingConfig.SeasonFolderFormat = seasonFolderFormat;
            _namingConfig.SpecialsFolderFormat = "MySpecials";

            Subject.BuildFilePath(fakeEpisodes, fakeSeries, fakeEpisodeFile, ".mkv").Should().Be(expectedPath.AsOsAgnostic());
        }

        [Test]
        public void should_clean_season_folder_when_it_contains_illegal_characters_in_series_title()
        {
            var filename = @"S01E05 - Episode Title";
            var seasonNumber = 1;
            var expectedPath = @"C:\Test\NCIS - Los Angeles\NCIS - Los Angeles Season 1\S01E05 - Episode Title.mkv";

            var fakeEpisodes = Builder<Episode>.CreateListOfSize(1)
                .All()
                .With(s => s.Title = "Episode Title")
                .With(s => s.SeasonNumber = seasonNumber)
                .With(s => s.EpisodeNumber = 5)
                .Build().ToList();
            var fakeSeries = Builder<Series>.CreateNew()
                .With(s => s.Title = "NCIS: Los Angeles")
                .With(s => s.Path = @"C:\Test\NCIS - Los Angeles".AsOsAgnostic())
                .With(s => s.SeasonFolder = true)
                .With(s => s.SeriesType = SeriesTypes.Standard)
                .Build();
            var fakeEpisodeFile = Builder<EpisodeFile>.CreateNew()
                .With(s => s.SceneName = filename)
                .Build();

            _namingConfig.SeasonFolderFormat = "{Series Title} Season {season:0}";

            Subject.BuildFilePath(fakeEpisodes, fakeSeries, fakeEpisodeFile, ".mkv").Should().Be(expectedPath.AsOsAgnostic());
        }
    }
}
