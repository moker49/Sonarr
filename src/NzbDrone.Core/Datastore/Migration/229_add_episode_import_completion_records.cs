using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(229)]
    public class add_episode_import_completion_records : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("EpisodeVersionImportEpisodes")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ImportId").AsInt32().NotNullable()
                .WithColumn("EpisodeId").AsInt32().NotNullable();
            Create.Index().OnTable("EpisodeVersionImportEpisodes").OnColumn("ImportId").Ascending().OnColumn("EpisodeId").Ascending().WithOptions().Unique();
            Execute.Sql("INSERT INTO \"EpisodeVersionImportEpisodes\" (\"ImportId\", \"EpisodeId\") SELECT \"ImportId\", \"EpisodeId\" FROM \"EpisodeVersionImportEpisodeLocks\"");

            Create.Table("EpisodeVersionImportCompletions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ImportId").AsInt32().NotNullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("PublishedAt").AsDateTime().Nullable();
            Create.Index().OnTable("EpisodeVersionImportCompletions").OnColumn("ImportId").Ascending().WithOptions().Unique();
        }
    }
}
