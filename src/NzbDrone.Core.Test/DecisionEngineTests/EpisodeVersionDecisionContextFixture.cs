using System;
using System.Collections.Generic;
using System.Linq;
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
                [1] = new List<ResolvedEpisodeVersion> { new ResolvedEpisodeVersion { EpisodeId = 1, SeriesId = 5, SeriesVersionId = 7, QualityProfileId = 20, ParentQualityProfileId = 10, EpisodeFileId = 100, Monitored = true, Label = "HD" } },
                [2] = new List<ResolvedEpisodeVersion> { new ResolvedEpisodeVersion { EpisodeId = 2, SeriesId = 5, SeriesVersionId = 7, QualityProfileId = 20, ParentQualityProfileId = 10, Monitored = true, Label = "HD" } }
            };
            Mocker.GetMock<IEpisodeVersionResolver>().Setup(v => v.ResolveMany(It.IsAny<IReadOnlyCollection<int>>())).Returns(() => _versions);
            Mocker.GetMock<IQualityProfileService>().Setup(p => p.Get(20)).Returns(new QualityProfile { Id = 20 });
            Mocker.GetMock<IMediaFileService>().Setup(f => f.GetFiles(It.IsAny<IEnumerable<int>>())).Returns(new List<EpisodeFile> { new EpisodeFile { Id = 100, SeriesId = 5 } });
        }

        [Test]
        public void expands_both_child_contexts_from_one_snapshot_in_definition_priority_order()
        {
            GivenBothVersions();
            var contexts = Subject.ForAllVersions(_source);

            contexts.Select(c => c.SeriesVersionId).Should().Equal(8, 7);
            contexts.Select(c => c.SeriesVersionPriority).Should().Equal(0, 1);
            contexts.Select(c => c.Series.QualityProfileId).Should().Equal(30, 20);
            contexts[0].Episodes.Should().OnlyContain(e => e.EpisodeFileId == 0);
            contexts[1].Episodes[0].EpisodeFileId.Should().Be(100);
            contexts[0].Episodes[0].Should().NotBeSameAs(contexts[1].Episodes[0]);
            _source.Series.QualityProfileId.Should().Be(10);
            _source.Episodes.Should().OnlyContain(e => e.EpisodeFileId == 200);
            Mocker.GetMock<IEpisodeVersionResolver>().Verify(v => v.ResolveMany(It.IsAny<IReadOnlyCollection<int>>()), Times.Once());
        }

        private void GivenBothVersions()
        {
            foreach (var entry in _versions)
            {
                entry.Value[0].CompositeProfileVersionId = 70;
                entry.Value[0].Priority = 1;
                entry.Value.Add(new ResolvedEpisodeVersion
                {
                    EpisodeId = entry.Key, SeriesId = 5, SeriesVersionId = 8, CompositeProfileVersionId = 80,
                    QualityProfileId = 30, ParentQualityProfileId = 10, Monitored = true, Label = "4K", Priority = 0, IsPrimary = true
                });
            }

            Mocker.GetMock<IQualityProfileService>().Setup(p => p.Get(30)).Returns(new QualityProfile { Id = 30 });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void rejects_incomplete_or_duplicate_version_coverage_before_returning_any_context(bool missing)
        {
            GivenBothVersions();
            if (missing)
            {
                _versions[2].RemoveAt(1);
            }
            else
            {
                _versions[2][1].SeriesVersionId = 7;
            }

            Action expand = () => Subject.ForAllVersions(_source);
            expand.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void expansion_rejects_changed_definition_metadata_across_a_pack()
        {
            GivenBothVersions();
            _versions[2][0].Priority = 0;
            Action expand = () => Subject.ForAllVersions(_source);
            expand.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void expansion_rejects_an_ordinary_episode_context()
        {
            Action expand = () => Subject.ForAllVersions(_source);
            expand.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void creates_isolated_child_profile_and_episode_file_context_for_a_pack()
        {
            var context = Subject.ForVersion(_source, 7);

            context.SeriesVersionId.Should().Be(7);
            context.SeriesVersionLabel.Should().Be("HD");
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
        public void rejects_inconsistent_or_unsafe_labels_in_a_pack()
        {
            _versions[2][0].Label = "4K";
            Action inconsistent = () => Subject.ForVersion(_source, 7);
            inconsistent.Should().Throw<InvalidOperationException>();
            _versions[2][0].Label = "../escape";
            _versions[1][0].Label = "../escape";
            Action unsafeLabel = () => Subject.ForVersion(_source, 7);
            unsafeLabel.Should().Throw<InvalidOperationException>();
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
