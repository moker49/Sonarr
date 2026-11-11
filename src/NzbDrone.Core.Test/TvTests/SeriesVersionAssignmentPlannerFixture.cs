using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class SeriesVersionAssignmentPlannerFixture : DbTest<SeriesVersionAssignmentPlanner, SeriesVersion>
    {
        private Series _series;
        private CompositeProfileVersion _sdr;
        private CompositeProfileVersion _hdr;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 5).BuildNew());
            _sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            _hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
        }

        private EpisodeFile CreateFile()
        {
            return Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
        }

        [Test]
        public void groups_a_shared_file_and_keeps_preview_read_only()
        {
            var file = CreateFile();
            var first = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = file.Id).BuildNew());
            var second = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = file.Id).BuildNew());

            var plan = Subject.Preview(_series.Id, 10, _hdr.Id);

            plan.ExistingFiles.Should().ContainSingle();
            plan.ExistingFiles[0].EpisodeFileId.Should().Be(file.Id);
            plan.ExistingFiles[0].EpisodeIds.Should().Equal(new[] { first.Id, second.Id }.OrderBy(id => id));
            plan.ExistingFiles[0].CompositeProfileVersionId.Should().Be(_hdr.Id);
            Mocker.Resolve<BasicRepository<Series>>().Get(_series.Id).QualityProfileId.Should().Be(5);
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void missing_files_need_an_explicit_valid_target_version()
        {
            var file = CreateFile();
            Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = file.Id).BuildNew());

            Action missing = () => Subject.Preview(_series.Id, 10, null);
            Action invalid = () => Subject.Preview(_series.Id, 10, _hdr.Id + 100);
            missing.Should().Throw<InvalidOperationException>();
            invalid.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void empty_series_needs_no_file_selection()
        {
            var plan = Subject.Preview(_series.Id, 10, null);
            plan.ExistingFiles.Should().BeEmpty();
            plan.Versions.Should().HaveCount(2);
        }

        [Test]
        public void rejects_unreferenced_files_and_cross_series_file_references()
        {
            var file = CreateFile();
            Action unreferenced = () => Subject.Preview(_series.Id, 10, _sdr.Id);
            unreferenced.Should().Throw<InvalidOperationException>();

            Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = file.Id).BuildNew());
            Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id + 100).With(e => e.EpisodeFileId = file.Id).BuildNew());
            Action crossSeries = () => Subject.Preview(_series.Id, 10, _sdr.Id);
            crossSeries.Should().Throw<InvalidOperationException>();
        }
    }
}
