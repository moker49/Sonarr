using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(230)]
    public class add_episode_download_completion_records : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeDownloadReservations").AddColumn("OriginalReleaseKey").AsString().Nullable();
            Create.Table("EpisodeDownloadReservationEpisodes")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ReservationId").AsInt32().NotNullable()
                .WithColumn("EpisodeId").AsInt32().NotNullable();
            Create.Index().OnTable("EpisodeDownloadReservationEpisodes").OnColumn("ReservationId").Ascending().OnColumn("EpisodeId").Ascending().WithOptions().Unique();
            Execute.Sql("INSERT INTO \"EpisodeDownloadReservationEpisodes\" (\"ReservationId\", \"EpisodeId\") SELECT \"ReservationId\", \"EpisodeId\" FROM \"EpisodeDownloadReservationItems\"");
            Create.Table("EpisodeVersionDownloadCompletions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ReservationId").AsInt32().NotNullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("PublishedAt").AsDateTime().Nullable();
            Create.Index().OnTable("EpisodeVersionDownloadCompletions").OnColumn("ReservationId").Ascending().WithOptions().Unique();
        }
    }
}
