using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(228)]
    public class add_registered_episode_import_file : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeVersionImportIntents").AddColumn("RegisteredFileId").AsInt32().Nullable();
        }
    }
}
