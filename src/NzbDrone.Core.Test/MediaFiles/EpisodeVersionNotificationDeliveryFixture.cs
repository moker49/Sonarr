using System;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeVersionNotificationDeliveryFixture : DbTest<EpisodeVersionNotificationDeliveryService, EpisodeVersionImportIntent>
    {
        private int _seriesId;
        private int _importId;

        [SetUp]
        public void Setup()
        {
            _seriesId = Db.Insert(Builder<Series>.CreateNew().BuildNew()).Id;
            _importId = Db.Insert(new EpisodeVersionImportIntent { SeriesId = _seriesId, SeriesVersionId = 1, ReservationId = 1, SourcePath = "source", DestinationPath = "destination", State = EpisodeVersionImportIntentState.Completed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }).Id;
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("INSERT INTO \"EpisodeVersionImportCompletions\" (\"ImportId\", \"CreatedAt\") VALUES (@id, @now)", new { id = _importId, now = DateTime.UtcNow });
        }

        [Test]
        public void retries_only_failed_recipients_and_marks_published_after_every_acknowledgement()
        {
            Subject.Capture(EpisodeVersionCompletionKind.Import, _seriesId, _importId, new[] { 7, 8 });
            var successfulCalls = 0;
            Subject.Deliver(EpisodeVersionCompletionKind.Import, _seriesId, _importId, 7, id => successfulCalls++).Should().BeTrue();
            Subject.Deliver(EpisodeVersionCompletionKind.Import, _seriesId, _importId, 8, id => throw new InvalidOperationException("Offline")).Should().BeFalse();
            Subject.Deliver(EpisodeVersionCompletionKind.Import, _seriesId, _importId, 7, id => successfulCalls++).Should().BeTrue();
            successfulCalls.Should().Be(1);
            var pending = Subject.GetPending(EpisodeVersionCompletionKind.Import, _seriesId, _importId).Single();
            pending.NotificationId.Should().Be(8);
            pending.Attempts.Should().Be(1);
            pending.LastError.Should().Be("Offline");
            using (var connection = Mocker.Resolve<IMainDatabase>().OpenConnection())
            {
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportCompletions\" WHERE \"PublishedAt\" IS NULL").Should().Be(1);
            }

            var stableId = 0;
            Subject.Deliver(EpisodeVersionCompletionKind.Import, _seriesId, _importId, 8, id => stableId = id).Should().BeTrue();

            stableId.Should().Be(pending.Id);
            Subject.GetPending(EpisodeVersionCompletionKind.Import, _seriesId, _importId).Should().BeEmpty();
            using var check = Mocker.Resolve<IMainDatabase>().OpenConnection();
            check.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportCompletions\" WHERE \"PublishedAt\" IS NOT NULL").Should().Be(1);
            check.ExecuteScalar<int>("SELECT \"Attempts\" FROM \"EpisodeVersionNotificationDeliveries\" WHERE \"Id\" = @stableId", new { stableId }).Should().Be(2);
        }

        [Test]
        public void recipient_capture_is_stable_and_uncaptured_recipients_cannot_be_sent()
        {
            Subject.Capture(EpisodeVersionCompletionKind.Import, _seriesId, _importId, new[] { 7 });
            Subject.Capture(EpisodeVersionCompletionKind.Import, _seriesId, _importId, new[] { 8 });
            Subject.GetPending(EpisodeVersionCompletionKind.Import, _seriesId, _importId).Should().ContainSingle(d => d.NotificationId == 7);
            Assert.Throws<InvalidOperationException>(() => Subject.Deliver(EpisodeVersionCompletionKind.Import, _seriesId, _importId, 8, id => Assert.Fail("Must not send")));
            Assert.Throws<InvalidOperationException>(() => Subject.GetPending(EpisodeVersionCompletionKind.Import, _seriesId + 100, _importId));
        }

        [Test]
        public void empty_recipient_snapshot_is_published_without_deliveries()
        {
            Subject.Capture(EpisodeVersionCompletionKind.Import, _seriesId, _importId, Array.Empty<int>());
            Subject.GetPending(EpisodeVersionCompletionKind.Import, _seriesId, _importId).Should().BeEmpty();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"EpisodeVersionImportCompletions\" WHERE \"PublishedAt\" IS NOT NULL").Should().Be(1);
        }

        [Test]
        public void retry_queue_defers_recent_attempts_and_excludes_published_completions()
        {
            var queue = Mocker.Resolve<EpisodeVersionNotificationQueue>();
            var pending = queue.GetDue().Should().ContainSingle().Which;
            pending.OwnerId.Should().Be(_importId);
            pending.Kind.Should().Be(EpisodeVersionCompletionKind.Import);

            queue.RecordAttempt(pending);
            queue.GetDue().Should().BeEmpty();
            using (var connection = Mocker.Resolve<IMainDatabase>().OpenConnection())
            {
                connection.Execute("UPDATE \"EpisodeVersionImportCompletions\" SET \"LastAttemptAt\" = @old", new { old = DateTime.UtcNow.AddMinutes(-6) });
            }
            queue.GetDue().Should().ContainSingle();
            Subject.Capture(EpisodeVersionCompletionKind.Import, _seriesId, _importId, Array.Empty<int>());
            queue.GetDue().Should().BeEmpty();
        }
    }
}
