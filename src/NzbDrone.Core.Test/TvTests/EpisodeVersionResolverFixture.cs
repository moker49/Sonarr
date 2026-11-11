using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class EpisodeVersionResolverFixture : DbTest<EpisodeVersionResolver, Episode>
    {
        private Series _series;
        private Episode _episode;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 10).With(s => s.Monitored = true).BuildNew());
            _episode = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.Monitored = true).With(e => e.EpisodeFileId = 0).BuildNew());
        }

        private (SeriesVersion, SeriesVersion) CreateVersions()
        {
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            return (Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = sdr.Id }), Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = hdr.Id }));
        }

        [Test]
        public void ordinary_episode_resolves_without_persisted_version_rows()
        {
            var resolved = Subject.Resolve(_episode.Id);
            resolved.Should().ContainSingle();
            resolved[0].QualityProfileId.Should().Be(10);
            resolved[0].SeriesVersionId.Should().BeNull();
            resolved[0].EpisodeFileId.Should().BeNull();
            resolved[0].Monitored.Should().BeTrue();
        }

        [Test]
        public void composite_episode_returns_each_version_and_its_owned_file()
        {
            var (sdr, hdr) = CreateVersions();
            var file = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            var owner = Db.Insert(new VersionEpisodeFile { SeriesVersionId = hdr.Id, EpisodeFileId = file.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = hdr.Id, EpisodeId = _episode.Id, VersionEpisodeFileId = owner.Id });

            var resolved = Subject.Resolve(_episode.Id);
            resolved.Select(v => v.Label).Should().Equal("SDR", "HDR");
            resolved[0].SeriesVersionId.Should().Be(sdr.Id);
            resolved[0].EpisodeFileId.Should().BeNull();
            resolved[1].EpisodeFileId.Should().Be(file.Id);
        }

        [Test]
        public void incomplete_definitions_fail_closed()
        {
            Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            Action incomplete = () => Subject.Resolve(_episode.Id);
            incomplete.Should().Throw<Exception>();
        }

        [Test]
        public void wrong_file_version_fails_closed()
        {
            var (sdr, hdr) = CreateVersions();
            var file = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            var owner = Db.Insert(new VersionEpisodeFile { SeriesVersionId = hdr.Id, EpisodeFileId = file.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = sdr.Id, EpisodeId = _episode.Id, VersionEpisodeFileId = owner.Id });

            Action resolve = () => Subject.Resolve(_episode.Id);
            resolve.Should().Throw<InvalidOperationException>();
        }
    }
}
