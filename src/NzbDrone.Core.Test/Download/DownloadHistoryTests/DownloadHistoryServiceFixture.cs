using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.DownloadHistoryTests
{
    [TestFixture]
    public class DownloadHistoryServiceFixture : CoreTest<DownloadHistoryService>
    {
        [Test]
        public void latest_state_is_scoped_to_client_and_version()
        {
            var expected = new DownloadHistory { DownloadClientId = 7, SeriesVersionId = 10, EventType = DownloadHistoryEventType.DownloadGrabbed };
            Mocker.GetMock<IDownloadHistoryRepository>().Setup(r => r.FindByDownloadId("job"))
                .Returns(new List<DownloadHistory>
                {
                    new DownloadHistory { DownloadClientId = 8, SeriesVersionId = 10, EventType = DownloadHistoryEventType.DownloadImported },
                    new DownloadHistory { DownloadClientId = 7, SeriesVersionId = 11, EventType = DownloadHistoryEventType.DownloadFailed },
                    new DownloadHistory { DownloadClientId = 7, SeriesVersionId = null, EventType = DownloadHistoryEventType.DownloadIgnored },
                    new DownloadHistory { DownloadClientId = 7, SeriesVersionId = 10, EventType = DownloadHistoryEventType.FileImported },
                    expected
                });

            Subject.GetLatestDownloadHistoryItem("job", 7, 10).Should().BeSameAs(expected);
            Subject.GetLatestDownloadHistoryItem("job", 9, 10).Should().BeNull();
        }

        [Test]
        public void ordinary_legacy_history_is_used_only_when_no_client_is_identified()
        {
            var legacy = new DownloadHistory { EventType = DownloadHistoryEventType.DownloadImported };
            var events = new List<DownloadHistory> { legacy };
            Mocker.GetMock<IDownloadHistoryRepository>().Setup(r => r.FindByDownloadId("job")).Returns(events);

            Subject.GetLatestDownloadHistoryItem("job", 7, null).Should().BeSameAs(legacy);
            Subject.GetLatestDownloadHistoryItem("job", 7, 10).Should().BeNull();

            events.Insert(0, new DownloadHistory { DownloadClientId = 8, EventType = DownloadHistoryEventType.DownloadGrabbed });
            Subject.GetLatestDownloadHistoryItem("job", 7, null).Should().BeNull();
        }
    }
}
