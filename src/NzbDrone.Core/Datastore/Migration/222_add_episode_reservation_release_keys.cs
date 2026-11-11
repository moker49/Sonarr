using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(222)]
    public class add_episode_reservation_release_keys : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("EpisodeDownloadReservationKeys")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ReservationId").AsInt32().NotNullable()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("ReleaseKey").AsString().NotNullable();
            Create.Index().OnTable("EpisodeDownloadReservationKeys")
                .OnColumn("SeriesVersionId").Ascending().OnColumn("ReleaseKey").Ascending().WithOptions().Unique();
            Create.Index().OnTable("EpisodeDownloadReservationKeys")
                .OnColumn("ReservationId").Ascending();
            Execute.Sql("INSERT INTO \"EpisodeDownloadReservationKeys\" (\"ReservationId\", \"SeriesVersionId\", \"ReleaseKey\") SELECT \"Id\", \"SeriesVersionId\", \"ReleaseKey\" FROM \"EpisodeDownloadReservations\"");
        }
    }
}
