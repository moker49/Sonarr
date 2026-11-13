using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class SeriesVersionStatusServiceFixture : DbTest<SeriesVersionStatusService, Episode>
    {
        private Series _series;
        private SeriesVersion _primary;
        private SeriesVersion _secondary;
        private Episode _first;
        private Episode _second;

        [SetUp]
        public void Setup()
        {
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 10).With(s => s.Monitored = true).BuildNew());
            var first = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var second = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _primary = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = first.Id });
            _secondary = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = second.Id });
            _first = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.SeasonNumber = 1).With(e => e.EpisodeNumber = 1).With(e => e.Monitored = true).With(e => e.EpisodeFileId = 0).BuildNew());
            _second = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.SeasonNumber = 1).With(e => e.EpisodeNumber = 2).With(e => e.Monitored = false).With(e => e.EpisodeFileId = 0).BuildNew());
        }

        private EpisodeFile CreateFile(int seriesId)
        {
            return Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = seriesId).With(f => f.RelativePath = "Season 1/shared.mkv").With(f => f.Size = 123).With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
        }

        [Test]
        public void reports_shared_files_and_reservations_only_for_their_version()
        {
            var file = CreateFile(_series.Id);
            var owner = Db.Insert(new VersionEpisodeFile { SeriesVersionId = _secondary.Id, EpisodeFileId = file.Id });
            foreach (var episode in new[] { _first, _second })
            {
                Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _secondary.Id, EpisodeId = episode.Id, VersionEpisodeFileId = owner.Id });
            }

            Db.Insert(new EpisodeDownloadReservationItem { ReservationId = 77, SeriesVersionId = _primary.Id, EpisodeId = _first.Id });

            var result = Subject.Get(_series.Id);

            result.Select(v => v.Label).Should().Equal("SDR", "HDR");
            result[0].Id.Should().Be(_primary.Id);
            result[0].IsPrimary.Should().BeTrue();
            result[0].Episodes.Select(e => e.EpisodeId).Should().Equal(_first.Id, _second.Id);
            result[0].Episodes.Should().OnlyContain(e => e.EpisodeFileId == null);
            result[0].Episodes[0].IsReserved.Should().BeTrue();
            result[0].Episodes[1].IsReserved.Should().BeFalse();
            result[0].Episodes[0].Monitored.Should().BeTrue();
            result[0].Episodes[1].Monitored.Should().BeFalse();
            result[1].Episodes.Should().OnlyContain(e => e.EpisodeFileId == file.Id && e.RelativePath == file.RelativePath && e.Size == 123 && !e.IsReserved);
            Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>().All().Should().HaveCount(2);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void rejects_files_owned_by_another_version_or_series(bool wrongVersion)
        {
            var file = CreateFile(wrongVersion ? _series.Id : _series.Id + 100);
            var owner = Db.Insert(new VersionEpisodeFile { SeriesVersionId = wrongVersion ? _secondary.Id : _primary.Id, EpisodeFileId = file.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _primary.Id, EpisodeId = _first.Id, VersionEpisodeFileId = owner.Id });

            Action get = () => Subject.Get(_series.Id);
            get.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void rejects_dangling_file_assignments()
        {
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _primary.Id, EpisodeId = _first.Id, VersionEpisodeFileId = 999 });
            Action get = () => Subject.Get(_series.Id);
            get.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void empty_composite_series_still_reports_both_versions()
        {
            Mocker.Resolve<BasicRepository<Episode>>().DeleteMany(new[] { _first.Id, _second.Id });
            var result = Subject.Get(_series.Id);
            result.Should().HaveCount(2).And.OnlyContain(v => v.Episodes.Count == 0);
        }

        [Test]
        public void retired_or_missing_versions_fail_closed()
        {
            _secondary.Retired = true;
            Mocker.Resolve<BasicRepository<SeriesVersion>>().Update(_secondary);
            Action get = () => Subject.Get(_series.Id);
            get.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void ordinary_series_has_no_explicit_versions()
        {
            var ordinary = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 20).With(s => s.TitleSlug = "ordinary").With(s => s.TvdbId = 222).BuildNew());
            Subject.Get(ordinary.Id).Should().BeEmpty();
        }

        [Test]
        public void missing_series_is_rejected()
        {
            Action get = () => Subject.Get(_series.Id + 100);
            get.Should().Throw<InvalidOperationException>();
        }
    }
}
