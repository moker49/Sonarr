using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(223)]
    public class preserve_reservation_download_id : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeDownloadReservations").AddColumn("NormalizedDownloadId").AsString().Nullable();
            Execute.Sql("UPDATE \"EpisodeDownloadReservations\" SET \"NormalizedDownloadId\" = UPPER(\"DownloadId\") WHERE \"DownloadId\" IS NOT NULL");
            Create.Index().OnTable("EpisodeDownloadReservations")
                .OnColumn("DownloadClientId").Ascending().OnColumn("NormalizedDownloadId").Ascending().WithOptions().Unique();
        }
    }
}
