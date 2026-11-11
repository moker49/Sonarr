using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(220)]
    public class add_episode_download_reservations : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("EpisodeDownloadReservations")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("ReleaseKey").AsString().NotNullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable();
            Create.Index().OnTable("EpisodeDownloadReservations")
                .OnColumn("SeriesVersionId").Ascending().OnColumn("ReleaseKey").Ascending().WithOptions().Unique();

            Create.Table("EpisodeDownloadReservationItems")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ReservationId").AsInt32().NotNullable()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("EpisodeId").AsInt32().NotNullable();
            Create.Index().OnTable("EpisodeDownloadReservationItems")
                .OnColumn("SeriesVersionId").Ascending().OnColumn("EpisodeId").Ascending().WithOptions().Unique();
            Create.Index().OnTable("EpisodeDownloadReservationItems")
                .OnColumn("ReservationId").Ascending();
        }
    }
}
