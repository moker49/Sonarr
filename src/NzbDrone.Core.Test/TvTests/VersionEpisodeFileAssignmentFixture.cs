using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class VersionEpisodeFileAssignmentFixture : DbTest<VersionEpisodeFileAssignmentService, EpisodeVersionAssignment>
    {
        private Series _series;
        private SeriesVersion _version;
        private EpisodeFile _file;
        private Episode _first;
        private Episode _second;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().BuildNew());
            _version = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = 1 });
            _file = Db.Insert(Builder<EpisodeFile>.CreateNew()
                .With(f => f.SeriesId = _series.Id)
                .With(f => f.Quality = new QualityModel())
                .With(f => f.Languages = new List<Language> { Language.English })
                .BuildNew());
            _first = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
            _second = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).BuildNew());
        }

        [Test]
        public void assigns_shared_file_to_all_selected_empty_episode_slots()
        {
            Subject.AssignUnoccupiedFile(_version.Id, _file.Id, new[] { _first.Id, _second.Id });

            AllStoredModels.Should().HaveCount(2);
            AllStoredModels.Should().OnlyContain(a => a.SeriesVersionId == _version.Id && a.VersionEpisodeFileId != null);
            AllStoredModels[0].VersionEpisodeFileId.Should().Be(AllStoredModels[1].VersionEpisodeFileId);
        }

        [Test]
        public void rejects_occupied_episode_without_creating_a_file_owner()
        {
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _version.Id, EpisodeId = _second.Id, VersionEpisodeFileId = 99 });

            Action assign = () => Subject.AssignUnoccupiedFile(_version.Id, _file.Id, new[] { _first.Id, _second.Id });
            assign.Should().Throw<InvalidOperationException>();
            Mocker.Resolve<BasicRepository<VersionEpisodeFile>>().All().Should().BeEmpty();
            AllStoredModels.Should().ContainSingle();
        }

        [Test]
        public void rejects_episode_from_another_series()
        {
            var other = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id + 100).BuildNew());
            Action assign = () => Subject.AssignUnoccupiedFile(_version.Id, _file.Id, new[] { _first.Id, other.Id });

            assign.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void rejects_a_file_already_owned_by_another_version()
        {
            var otherVersion = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = 2 });
            Db.Insert(new VersionEpisodeFile { SeriesVersionId = otherVersion.Id, EpisodeFileId = _file.Id });

            Action assign = () => Subject.AssignUnoccupiedFile(_version.Id, _file.Id, new[] { _first.Id });
            assign.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void rejects_retired_version_and_duplicate_episode_ids()
        {
            _version.Retired = true;
            Mocker.Resolve<BasicRepository<SeriesVersion>>().Update(_version);

            Action retired = () => Subject.AssignUnoccupiedFile(_version.Id, _file.Id, new[] { _first.Id });
            Action duplicate = () => Subject.AssignUnoccupiedFile(_version.Id, _file.Id, new[] { _first.Id, _first.Id });
            retired.Should().Throw<InvalidOperationException>();
            duplicate.Should().Throw<ArgumentException>();
            AllStoredModels.Should().BeEmpty();
        }
    }
}
