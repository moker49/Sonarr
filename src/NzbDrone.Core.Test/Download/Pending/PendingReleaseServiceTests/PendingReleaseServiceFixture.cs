using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class PendingReleaseServiceFixture : CoreTest<PendingReleaseService>
    {
        private void GivenPendingRelease()
        {
            Mocker.GetMock<IPendingReleaseRepository>()
                              .Setup(v => v.All())
                              .Returns(new List<PendingRelease>
                                {
                                      new PendingRelease { Release = new ReleaseInfo { IndexerId = 1 } }
                                });
        }

        [Test]
        public void retry_metadata_is_isolated_for_versions_sharing_a_torrent_release()
        {
            var torrent = new TorrentInfo { IndexerId = 1, MagnetUrl = "magnet:test", InfoHash = "hash" };
            Mocker.GetMock<IIndexerStatusService>().Setup(s => s.GetBlockedProviders()).Returns(new List<IndexerStatus>());
            Mocker.GetMock<IPendingReleaseRepository>().Setup(s => s.All()).Returns(new List<PendingRelease>
            {
                new PendingRelease { Release = torrent, SeriesVersionId = 7, Reason = PendingReleaseReason.Delay },
                new PendingRelease { Release = torrent, SeriesVersionId = 8, Reason = PendingReleaseReason.Fallback }
            });

            var releases = Subject.GetPending();
            releases.Should().HaveCount(2);
            releases[0].PendingSeriesVersionId.Should().Be(7);
            releases[1].PendingSeriesVersionId.Should().Be(8);
            releases[0].PendingReleaseReason.Should().Be(PendingReleaseReason.Delay);
            releases[1].PendingReleaseReason.Should().Be(PendingReleaseReason.Fallback);
            releases[0].Should().BeOfType<TorrentInfo>().Which.MagnetUrl.Should().Be(torrent.MagnetUrl);
            releases[0].Should().NotBeSameAs(releases[1]);
            torrent.PendingSeriesVersionId.Should().BeNull();
            torrent.PendingReleaseReason.Should().BeNull();
        }

        [Test]
        public void should_not_ignore_pending_items_from_available_indexer()
        {
            Mocker.GetMock<IIndexerStatusService>()
                .Setup(v => v.GetBlockedProviders())
                .Returns(new List<IndexerStatus>());

            GivenPendingRelease();

            var results = Subject.GetPending();

            results.Should().NotBeEmpty();
            Mocker.GetMock<IMakeDownloadDecision>()
                  .Verify(v => v.GetRssDecision(It.Is<List<ReleaseInfo>>(d => d.Count == 0), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_ignore_pending_items_from_unavailable_indexer()
        {
            Mocker.GetMock<IIndexerStatusService>()
                .Setup(v => v.GetBlockedProviders())
                .Returns(new List<IndexerStatus> { new IndexerStatus { ProviderId = 1, DisabledTill = DateTime.UtcNow.AddHours(2) } });

            GivenPendingRelease();

            var results = Subject.GetPending();

            results.Should().BeEmpty();
        }
    }
}
