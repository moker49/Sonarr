using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(233)]
    public class add_episode_reservation_grab_history_marker : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeDownloadReservations").AddColumn("GrabRecordedAt").AsDateTime().Nullable();
        }
    }
}
