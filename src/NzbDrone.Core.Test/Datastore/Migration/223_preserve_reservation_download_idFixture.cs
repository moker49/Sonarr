using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class preserve_reservation_download_idFixture : MigrationTest<preserve_reservation_download_id>
    {
        [Test]
        public void preserves_client_id_and_backfills_normalized_lookup()
        {
            var db = WithMigrationTestDb(c => c.Insert.IntoTable("EpisodeDownloadReservations").Row(new
            {
                Id = 55,
                SeriesId = 2,
                SeriesVersionId = 3,
                ReleaseKey = "key",
                CreatedAt = DateTime.UtcNow,
                State = 2,
                DownloadClientId = 5,
                DownloadId = "Job-AbC"
            }));

            var row = db.Query<Reservation>("SELECT \"DownloadId\", \"NormalizedDownloadId\" FROM \"EpisodeDownloadReservations\"").Single();
            row.DownloadId.Should().Be("Job-AbC");
            row.NormalizedDownloadId.Should().Be("JOB-ABC");
        }

        private class Reservation
        {
            public string DownloadId { get; set; }
            public string NormalizedDownloadId { get; set; }
        }
    }
}
