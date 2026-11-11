using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(221)]
    public class add_episode_reservation_lifecycle : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeDownloadReservations").AddColumn("State").AsInt32().NotNullable().WithDefaultValue(0);
            Alter.Table("EpisodeDownloadReservations").AddColumn("DownloadClientId").AsInt32().Nullable();
            Alter.Table("EpisodeDownloadReservations").AddColumn("DownloadId").AsString().Nullable();
            Alter.Table("EpisodeDownloadReservations").AddColumn("UpdatedAt").AsDateTime().Nullable();
            Alter.Table("EpisodeDownloadReservations").AddColumn("Note").AsString().Nullable();
            Create.Index().OnTable("EpisodeDownloadReservations")
                .OnColumn("DownloadClientId").Ascending().OnColumn("DownloadId").Ascending().WithOptions().Unique();
        }
    }
}
