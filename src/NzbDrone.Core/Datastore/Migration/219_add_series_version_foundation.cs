using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(219)]
    public class add_series_version_foundation : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("CompositeProfileVersions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("CompositeProfileId").AsInt32().NotNullable()
                .WithColumn("QualityProfileId").AsInt32().NotNullable()
                .WithColumn("Label").AsString().NotNullable()
                .WithColumn("Priority").AsInt32().NotNullable()
                .WithColumn("IsPrimary").AsBoolean().NotNullable();

            Create.Index().OnTable("CompositeProfileVersions")
                .OnColumn("CompositeProfileId").Ascending().OnColumn("Priority").Ascending().WithOptions().Unique();
            Create.Index().OnTable("CompositeProfileVersions")
                .OnColumn("CompositeProfileId").Ascending().OnColumn("QualityProfileId").Ascending().WithOptions().Unique();

            Create.Table("SeriesVersions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("SeriesId").AsInt32().NotNullable()
                .WithColumn("CompositeProfileVersionId").AsInt32().NotNullable()
                .WithColumn("Retired").AsBoolean().NotNullable().WithDefaultValue(false);
            Create.Index().OnTable("SeriesVersions")
                .OnColumn("SeriesId").Ascending().OnColumn("CompositeProfileVersionId").Ascending().WithOptions().Unique();

            // A physical file has one version owner, but can cover multiple episodes.
            Create.Table("VersionEpisodeFiles")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("EpisodeFileId").AsInt32().NotNullable();
            Create.Index().OnTable("VersionEpisodeFiles")
                .OnColumn("EpisodeFileId").Ascending().WithOptions().Unique();

            Create.Table("EpisodeVersionAssignments")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("SeriesVersionId").AsInt32().NotNullable()
                .WithColumn("EpisodeId").AsInt32().NotNullable()
                .WithColumn("VersionEpisodeFileId").AsInt32().Nullable();
            Create.Index().OnTable("EpisodeVersionAssignments")
                .OnColumn("SeriesVersionId").Ascending().OnColumn("EpisodeId").Ascending().WithOptions().Unique();
            Create.Index().OnTable("EpisodeVersionAssignments")
                .OnColumn("VersionEpisodeFileId").Ascending();
        }
    }
}
