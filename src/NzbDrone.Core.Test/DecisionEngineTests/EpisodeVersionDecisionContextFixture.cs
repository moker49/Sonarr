using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class EpisodeVersionDecisionContextFixture : CoreTest<EpisodeVersionDecisionContext>
    {
        private RemoteEpisode _source;
        private Dictionary<int, List<ResolvedEpisodeVersion>> _versions;

        [SetUp]
        public void Setup()
        {
            var series = new Series { Id = 5, QualityProfileId = 10, Monitored = true, QualityProfile = new QualityProfile { Id = 10 } };
            _source = new RemoteEpisode
            {
                Series = series,
                Episodes = new List<Episode>
                {
                    new Episode { Id = 1, SeriesId = 5, EpisodeFileId = 200, Monitored = true },
                    new Episode { Id = 2, SeriesId = 5, EpisodeFileId = 200, Monitored = true }
                }
            };
            _versions = new Dictionary<int, List<ResolvedEpisodeVersion>>
            {
                [1] = new List<ResolvedEpisodeVersion> { new ResolvedEpisodeVersion { EpisodeId = 1, SeriesId = 5, SeriesVersionId = 7, QualityProfileId = 20, ParentQualityProfileId = 10, EpisodeFileId = 100, Monitored = true } },
                [2] = new List<ResolvedEpisodeVersion> { new ResolvedEpisodeVersion { EpisodeId = 2, SeriesId = 5, SeriesVersionId = 7, QualityProfileId = 20, ParentQualityProfileId = 10, Monitored = true } }
            };
            Mocker.GetMock<IEpisodeVersionResolver>().Setup(v => v.ResolveMany(It.IsAny<IReadOnlyCollection<int>>())).Returns(() => _versions);
            Mocker.GetMock<IQualityProfileService>().Setup(p => p.Get(20)).Returns(new QualityProfile { Id = 20 });
            Mocker.GetMock<IMediaFileService>().Setup(f => f.GetFiles(It.IsAny<IEnumerable<int>>())).Returns(new List<EpisodeFile> { new EpisodeFile { Id = 100, SeriesId = 5 } });
        }

        [Test]
        public void creates_isolated_child_profile_and_episode_file_context_for_a_pack()
        {
            var context = Subject.ForVersion(_source, 7);

            context.SeriesVersionId.Should().Be(7);
            context.Series.QualityProfileId.Should().Be(20);
            context.Series.QualityProfile.Value.Id.Should().Be(20);
            context.Episodes[0].EpisodeFileId.Should().Be(100);
            context.Episodes[0].EpisodeFile.Value.Id.Should().Be(100);
            context.Episodes[1].EpisodeFileId.Should().Be(0);
            context.Episodes[1].EpisodeFile.Value.Should().BeNull();
            context.Episodes[0].Series.Should().BeSameAs(context.Series);
            _source.Series.QualityProfileId.Should().Be(10);
            _source.Episodes.Should().OnlyContain(e => e.EpisodeFileId == 200);
        }

        [Test]
        public void fails_closed_when_an_episode_does_not_belong_to_the_requested_version()
        {
            _versions[2].Clear();
            Action create = () => Subject.ForVersion(_source, 7);
            create.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void fails_closed_when_series_or_file_ownership_changed()
        {
            _versions[1][0].ParentQualityProfileId = 99;
            Action changedProfile = () => Subject.ForVersion(_source, 7);
            changedProfile.Should().Throw<InvalidOperationException>();

            _versions[1][0].ParentQualityProfileId = 10;
            Mocker.GetMock<IMediaFileService>().Setup(f => f.GetFiles(It.IsAny<IEnumerable<int>>())).Returns(new List<EpisodeFile> { new EpisodeFile { Id = 100, SeriesId = 999 } });
            Action wrongFile = () => Subject.ForVersion(_source, 7);
            wrongFile.Should().Throw<InvalidOperationException>();
        }
    }
}
