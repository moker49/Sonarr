using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionNotificationRetryFixture : CoreTest<EpisodeVersionNotificationRetryService>
    {
        [Test]
        public void failed_import_notification_does_not_prevent_other_completions_being_processed()
        {
            var first = new PendingEpisodeVersionNotification { Kind = EpisodeVersionCompletionKind.Import, SeriesId = 1, OwnerId = 2 };
            var second = new PendingEpisodeVersionNotification { Kind = EpisodeVersionCompletionKind.Download, SeriesId = 1, OwnerId = 3 };
            Mocker.GetMock<IEpisodeVersionNotificationQueue>().Setup(q => q.GetDue()).Returns(new List<PendingEpisodeVersionNotification> { first, second });
            Mocker.GetMock<IEpisodeVersionImportNotificationService>().Setup(s => s.Deliver(1, 2)).Throws(new InvalidOperationException("Unavailable"));

            Subject.Execute(new RetryEpisodeVersionNotificationsCommand());

            Mocker.GetMock<IEpisodeVersionNotificationQueue>().Verify(q => q.RecordAttempt(first), Times.Once());
            Mocker.GetMock<IEpisodeVersionNotificationQueue>().Verify(q => q.RecordAttempt(second), Times.Once());
            Mocker.GetMock<IEpisodeVersionDownloadNotificationService>().Verify(s => s.Deliver(1, 3), Times.Once());
            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
