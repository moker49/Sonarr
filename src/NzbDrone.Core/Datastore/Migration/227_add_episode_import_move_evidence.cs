using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(227)]
    public class add_episode_import_move_evidence : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeVersionImportIntents").AddColumn("SourceSize").AsInt64().Nullable();
            Alter.Table("EpisodeVersionImportIntents").AddColumn("SourceHash").AsString(64).Nullable();
            Alter.Table("EpisodeVersionImportIntents").AddColumn("CopyOnly").AsBoolean().Nullable();
            Alter.Table("EpisodeVersionImportIntents").AddColumn("Note").AsString(2048).Nullable();
        }
    }
}
