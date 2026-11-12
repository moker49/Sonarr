using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(225)]
    public class add_version_to_download_history : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("History").AddColumn("SeriesVersionId").AsInt32().Nullable();
            Alter.Table("DownloadHistory").AddColumn("SeriesVersionId").AsInt32().Nullable();
            Alter.Table("PendingReleases").AddColumn("SeriesVersionId").AsInt32().Nullable();
            Create.Index().OnTable("History").OnColumn("EpisodeId").Ascending().OnColumn("SeriesVersionId").Ascending();
            Create.Index().OnTable("PendingReleases").OnColumn("SeriesId").Ascending().OnColumn("SeriesVersionId").Ascending();
        }
    }
}
