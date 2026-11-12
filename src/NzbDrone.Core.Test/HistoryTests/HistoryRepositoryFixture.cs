using System.Collections.Generic;
using System;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.HistoryTests
{
    [TestFixture]
    public class HistoryRepositoryFixture : DbTest<HistoryRepository, EpisodeHistory>
    {
        private Series _series1;
        private Series _series2;

        [SetUp]
        public void Setup()
        {
            _series1 = Builder<Series>.CreateNew()
                                      .With(s => s.Id = 7)
                                      .Build();

            _series2 = Builder<Series>.CreateNew()
                                      .With(s => s.Id = 8)
                                      .Build();
        }

        [Test]
        public void should_read_write_dictionary()
        {
            var history = Builder<EpisodeHistory>.CreateNew()
                .With(c => c.Languages = new List<Language> { Language.English })
                .With(c => c.Quality = new QualityModel())
                .BuildNew();

            history.Data.Add("key1", "value1");
            history.Data.Add("key2", "value2");

            Subject.Insert(history);

            StoredModel.Data.Should().HaveCount(2);
        }

        [Test]
        public void most_recent_version_history_ignores_newer_sibling_records()
        {
            var older = Builder<EpisodeHistory>.CreateNew().With(h => h.EpisodeId = 1).With(h => h.SeriesVersionId = 10).With(h => h.Date = DateTime.UtcNow.AddMinutes(-2)).With(h => h.Quality = new QualityModel()).With(h => h.Languages = new List<Language> { Language.English }).BuildNew();
            var sibling = Builder<EpisodeHistory>.CreateNew().With(h => h.EpisodeId = 1).With(h => h.SeriesVersionId = 11).With(h => h.Date = DateTime.UtcNow).With(h => h.Quality = new QualityModel()).With(h => h.Languages = new List<Language> { Language.English }).BuildNew();
            Subject.Insert(older);
            Subject.Insert(sibling);

            Subject.MostRecentForEpisodeVersion(1, 10).Id.Should().Be(older.Id);
            Subject.MostRecentForEpisodeVersion(1, 12).Should().BeNull();
        }

        [Test]
        public void ordinary_history_ignores_newer_version_records()
        {
            var ordinary = Builder<EpisodeHistory>.CreateNew().With(h => h.EpisodeId = 1).With(h => h.SeriesVersionId = null).With(h => h.Date = DateTime.UtcNow.AddMinutes(-2)).With(h => h.Quality = new QualityModel()).With(h => h.Languages = new List<Language> { Language.English }).BuildNew();
            var versioned = Builder<EpisodeHistory>.CreateNew().With(h => h.EpisodeId = 1).With(h => h.SeriesVersionId = 10).With(h => h.Date = DateTime.UtcNow).With(h => h.Quality = new QualityModel()).With(h => h.Languages = new List<Language> { Language.English }).BuildNew();
            Subject.Insert(ordinary);
            Subject.Insert(versioned);

            Subject.MostRecentForOrdinaryEpisode(1).Id.Should().Be(ordinary.Id);
        }

        [Test]
        public void should_get_download_history()
        {
            var historyBluray = Builder<EpisodeHistory>.CreateNew()
                .With(c => c.Languages = new List<Language> { Language.English })
                .With(c => c.Quality = new QualityModel(Quality.Bluray1080p))
                .With(c => c.SeriesId = 12)
                .With(c => c.EventType = EpisodeHistoryEventType.Grabbed)
                .BuildNew();

            var historyDvd = Builder<EpisodeHistory>.CreateNew()
                .With(c => c.Languages = new List<Language> { Language.English })
                .With(c => c.Quality = new QualityModel(Quality.DVD))
                .With(c => c.SeriesId = 12)
                .With(c => c.EventType = EpisodeHistoryEventType.Grabbed)
             .BuildNew();

            Subject.Insert(historyBluray);
            Subject.Insert(historyDvd);

            var downloadHistory = Subject.FindDownloadHistory(12, new QualityModel(Quality.Bluray1080p));

            downloadHistory.Should().HaveCount(1);
        }

        [Test]
        public void should_delete_history_items_by_seriesId()
        {
            var items = Builder<EpisodeHistory>.CreateListOfSize(5)
                .TheFirst(1)
                .With(c => c.SeriesId = _series2.Id)
                .TheRest()
                .With(c => c.SeriesId = _series1.Id)
                .All()
                .With(c => c.Id = 0)
                .With(c => c.Quality = new QualityModel(Quality.Bluray1080p))
                .With(c => c.Languages = new List<Language> { Language.English })
                .With(c => c.EventType = EpisodeHistoryEventType.Grabbed)
                .BuildListOfNew();

            Db.InsertMany(items);

            Subject.DeleteForSeries(new List<int> { _series1.Id });

            var dbItems = Subject.All();
            var removedItems = dbItems.Where(h => h.SeriesId == _series1.Id);
            var nonRemovedItems = dbItems.Where(h => h.SeriesId == _series2.Id);

            removedItems.Should().HaveCount(0);
            nonRemovedItems.Should().HaveCount(1);
        }
    }
}
