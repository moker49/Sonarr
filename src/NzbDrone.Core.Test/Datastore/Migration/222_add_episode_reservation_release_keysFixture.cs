using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class add_episode_reservation_release_keysFixture : MigrationTest<add_episode_reservation_release_keys>
    {
        [Test]
        public void backfills_keys_for_existing_reservations()
        {
            var db = WithMigrationTestDb(c => c.Insert.IntoTable("EpisodeDownloadReservations").Row(new
            {
                Id = 41,
                SeriesId = 2,
                SeriesVersionId = 3,
                ReleaseKey = "legacy-key",
                CreatedAt = DateTime.UtcNow,
                State = 0
            }));

            var key = db.Query<ReservationKey>("SELECT \"ReservationId\", \"SeriesVersionId\", \"ReleaseKey\" FROM \"EpisodeDownloadReservationKeys\"").Single();
            key.ReservationId.Should().Be(41);
            key.SeriesVersionId.Should().Be(3);
            key.ReleaseKey.Should().Be("legacy-key");
        }

        private class ReservationKey
        {
            public int ReservationId { get; set; }
            public int SeriesVersionId { get; set; }
            public string ReleaseKey { get; set; }
        }
    }
}
