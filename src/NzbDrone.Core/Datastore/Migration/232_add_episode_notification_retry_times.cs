using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(232)]
    public class add_episode_notification_retry_times : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeVersionImportCompletions").AddColumn("LastAttemptAt").AsDateTime().Nullable();
            Alter.Table("EpisodeVersionDownloadCompletions").AddColumn("LastAttemptAt").AsDateTime().Nullable();
        }
    }
}
