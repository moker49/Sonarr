using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class EpisodeVersionDownloadDispatchFixture : CoreTest<EpisodeVersionDownloadDispatchService>
    {
        private RemoteEpisode _source;
        private Mock<IDownloadClient> _client;

        [SetUp]
        public void Setup()
        {
            var series = new Series { Id = 1 };
            var episodes = new List<Episode> { new Episode { Id = 5, SeriesId = 1 } };
            _source = new RemoteEpisode { Series = series, Episodes = episodes, SeriesVersionId = 3, Release = new ReleaseInfo { Guid = "release", Title = "Show", Size = 123 } };
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(1)).Returns(series);
            Mocker.GetMock<IEpisodeService>().Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>())).Returns(episodes);
            Mocker.GetMock<IEpisodeVersionDecisionContext>().Setup(s => s.ForVersion(It.IsAny<RemoteEpisode>(), 3)).Returns(_source);
            Mocker.GetMock<IMakeDownloadDecision>().Setup(s => s.GetMappedDecision(_source)).Returns(new DownloadDecision(_source));
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.ReserveEmptySlots(3, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<int>>())).Returns(12);
            _client = new Mock<IDownloadClient>();
            _client.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 7 });
        }

        [Test]
        public async Task reserves_and_begins_dispatch_before_client_submission_then_binds_returned_identity()
        {
            var begun = false;
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.BeginDispatch(12, 7)).Callback(() => begun = true);
            _client.Setup(c => c.Download(_source, null)).Returns(() =>
            {
                begun.Should().BeTrue();
                return Task.FromResult("job");
            });

            var result = await Subject.Submit(_source, _client.Object, null);

            result.ReservationId.Should().Be(12);
            result.DownloadId.Should().Be("job");
            Mocker.GetMock<IEpisodeDownloadReservationService>().Verify(s => s.Submitted(12, "job"), Times.Once());
        }

        [Test]
        public void timeout_retains_reservation_as_uncertain_without_freeing_episode_slots()
        {
            _client.Setup(c => c.Download(_source, null)).ThrowsAsync(new TimeoutException());

            Assert.ThrowsAsync<TimeoutException>(() => Subject.Submit(_source, _client.Object, null));

            Mocker.GetMock<IEpisodeDownloadReservationService>().Verify(s => s.MarkUncertain(12, It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IEpisodeDownloadReservationService>().Verify(s => s.Release(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IEpisodeDownloadReservationService>().Verify(s => s.Submitted(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void missing_client_id_or_binding_failure_requires_reconciliation()
        {
            _client.Setup(c => c.Download(_source, null)).ReturnsAsync((string)null);
            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.Submit(_source, _client.Object, null));
            _client.Setup(c => c.Download(_source, null)).ReturnsAsync("job");
            Mocker.GetMock<IEpisodeDownloadReservationService>().Setup(s => s.Submitted(12, "job")).Throws(new InvalidOperationException("Already bound"));
            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.Submit(_source, _client.Object, null));
            Mocker.GetMock<IEpisodeDownloadReservationService>().Verify(s => s.MarkUncertain(12, It.IsAny<string>()), Times.Exactly(2));
        }

        [Test]
        public void occupied_version_slots_never_contact_the_client()
        {
            _source.Episodes[0].EpisodeFileId = 99;
            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.Submit(_source, _client.Object, null));
            _client.Verify(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()), Times.Never());
            Mocker.GetMock<IEpisodeDownloadReservationService>().Verify(s => s.ReserveEmptySlots(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<int>>()), Times.Never());
        }
    }
}
