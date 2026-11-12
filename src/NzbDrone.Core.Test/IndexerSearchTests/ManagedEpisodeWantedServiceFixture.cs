using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class ManagedEpisodeWantedServiceFixture : DbTest<ManagedEpisodeWantedService, Episode>
    {
        private Series _series;
        private SeriesVersion _sdr;
        private SeriesVersion _hdr;
        private Episode _first;
        private Episode _second;
        private DateTime _now;

        [SetUp]
        public void Setup()
        {
            _now = DateTime.UtcNow;
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 10).With(s => s.Monitored = true).BuildNew());
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _sdr = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = sdr.Id });
            _hdr = Db.Insert(new SeriesVersion { SeriesId = _series.Id, CompositeProfileVersionId = hdr.Id });
            _first = AddEpisode(1, true, _now.AddDays(-1));
            _second = AddEpisode(1, true, _now.AddDays(-1));
        }

        private Episode AddEpisode(int season, bool monitored, DateTime? airDate)
        {
            return Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.SeasonNumber = season).With(e => e.Monitored = monitored).With(e => e.AirDateUtc = airDate).BuildNew());
        }

        [Test]
        public void returns_only_missing_unreserved_versions_even_when_one_file_covers_multiple_episodes()
        {
            var file = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            var owner = Db.Insert(new VersionEpisodeFile { SeriesVersionId = _sdr.Id, EpisodeFileId = file.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _sdr.Id, EpisodeId = _first.Id, VersionEpisodeFileId = owner.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _sdr.Id, EpisodeId = _second.Id, VersionEpisodeFileId = owner.Id });
            Db.Insert(new EpisodeDownloadReservationItem { ReservationId = 77, SeriesVersionId = _hdr.Id, EpisodeId = _second.Id });

            var result = Subject.Missing(_series.Id, true, false, _now);

            result.Should().ContainSingle();
            result[0].EpisodeId.Should().Be(_first.Id);
            result[0].SeriesVersionId.Should().Be(_hdr.Id);
            result[0].Label.Should().Be("HDR");
        }

        [Test]
        public void honors_air_dates_monitoring_and_specials()
        {
            var unmonitored = AddEpisode(1, false, _now.AddDays(-1));
            var special = AddEpisode(0, true, _now.AddDays(-1));
            AddEpisode(1, true, _now.AddDays(1));
            AddEpisode(1, true, null);

            Subject.Missing(null, true, false, _now).Select(r => r.EpisodeId).Distinct().Should().BeEquivalentTo(new[] { _first.Id, _second.Id });
            Subject.Missing(null, false, false, _now).Select(r => r.EpisodeId).Distinct().Should().Equal(unmonitored.Id);
            Subject.Missing(null, true, true, _now).Select(r => r.EpisodeId).Distinct().Should().Contain(special.Id);
        }

        [Test]
        public void fails_closed_for_missing_or_conflicting_version_ownership()
        {
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _sdr.Id, EpisodeId = _first.Id, VersionEpisodeFileId = 99999 });
            Action missingFile = () => Subject.Missing(_series.Id, true, true, _now);
            missingFile.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void fails_closed_if_series_version_is_missing()
        {
            Mocker.Resolve<NzbDrone.Core.Datastore.BasicRepository<SeriesVersion>>().Delete(_hdr.Id);
            Action missingVersion = () => Subject.Missing(_series.Id, true, true, _now);
            missingVersion.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void cutoff_uses_each_version_profile_and_excludes_reserved_episodes()
        {
            var profiles = new List<QualityProfile>
            {
                new QualityProfile { Id = 1, UpgradeAllowed = true, Cutoff = Quality.HDTV720p.Id, Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.SDTV, Allowed = true }, new QualityProfileQualityItem { Quality = Quality.HDTV720p, Allowed = true } } },
                new QualityProfile { Id = 2, UpgradeAllowed = true, Cutoff = Quality.SDTV.Id, Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.SDTV, Allowed = true }, new QualityProfileQualityItem { Quality = Quality.HDTV720p, Allowed = true } } }
            };
            Mocker.GetMock<IQualityProfileService>().Setup(p => p.All()).Returns(profiles);
            var firstFile = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel(Quality.SDTV)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            var secondFile = Db.Insert(Builder<EpisodeFile>.CreateNew().With(f => f.SeriesId = _series.Id).With(f => f.Quality = new QualityModel(Quality.SDTV)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            var sdrFile = Db.Insert(new VersionEpisodeFile { SeriesVersionId = _sdr.Id, EpisodeFileId = firstFile.Id });
            var hdrFile = Db.Insert(new VersionEpisodeFile { SeriesVersionId = _hdr.Id, EpisodeFileId = secondFile.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _sdr.Id, EpisodeId = _first.Id, VersionEpisodeFileId = sdrFile.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _sdr.Id, EpisodeId = _second.Id, VersionEpisodeFileId = sdrFile.Id });
            Db.Insert(new EpisodeVersionAssignment { SeriesVersionId = _hdr.Id, EpisodeId = _first.Id, VersionEpisodeFileId = hdrFile.Id });
            Db.Insert(new EpisodeDownloadReservationItem { ReservationId = 77, SeriesVersionId = _sdr.Id, EpisodeId = _second.Id });

            var result = Subject.CutoffUnmet(_series.Id, true, false);

            result.Should().ContainSingle();
            result[0].EpisodeId.Should().Be(_first.Id);
            result[0].SeriesVersionId.Should().Be(_sdr.Id);
        }
    }
}
