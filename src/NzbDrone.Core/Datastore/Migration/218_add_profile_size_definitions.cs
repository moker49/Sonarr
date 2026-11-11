using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(218)]
    public class add_profile_size_definitions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("QualityProfiles").AddColumn("SizeDefinitions").AsString().Nullable();
            Alter.Table("QualityProfiles").AddColumn("UseGlobalSizeDefinitions").AsBoolean().Nullable();
        }
    }
}
