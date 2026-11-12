using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(226)]
    public class add_episode_version_import_intents : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("EpisodeVersionImportIntents")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("ReservationId").AsInt32().NotNullable()
                .WithColumn("SourcePath").AsString(4096).NotNullable()
                .WithColumn("DestinationPath").AsString(4096).NotNullable()
                .WithColumn("State").AsInt32().NotNullable()
                .WithColumn("CreatedAt").AsDateTime().NotNullable()
                .WithColumn("UpdatedAt").AsDateTime().NotNullable();
            Create.Index().OnTable("EpisodeVersionImportIntents").OnColumn("SeriesId").Ascending();

            Create.Table("EpisodeVersionImportEpisodeLocks")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ImportId").AsInt32().NotNullable()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("EpisodeId").AsInt32().NotNullable();
            Create.Index().OnTable("EpisodeVersionImportEpisodeLocks").OnColumn("SeriesVersionId").Ascending().OnColumn("EpisodeId").Ascending().WithOptions().Unique();
            Create.Index().OnTable("EpisodeVersionImportEpisodeLocks").OnColumn("ImportId").Ascending();

            Create.Table("EpisodeVersionImportPathLocks")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("ImportId").AsInt32().NotNullable()
                .WithColumn("PathHash").AsString(64).NotNullable();
            Create.Index().OnTable("EpisodeVersionImportPathLocks").OnColumn("PathHash").Ascending().WithOptions().Unique();
            Create.Index().OnTable("EpisodeVersionImportPathLocks").OnColumn("ImportId").Ascending();
        }
    }
}
