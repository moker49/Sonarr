using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(231)]
    public class add_episode_version_notification_deliveries : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("EpisodeVersionImportCompletions").AddColumn("RecipientsCapturedAt").AsDateTime().Nullable();
            Alter.Table("EpisodeVersionDownloadCompletions").AddColumn("RecipientsCapturedAt").AsDateTime().Nullable();
            Create.Table("EpisodeVersionNotificationDeliveries")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("Kind").AsInt32().NotNullable()
                .WithColumn("CompletionId").AsInt32().NotNullable()
                .WithColumn("NotificationId").AsInt32().NotNullable()
                .WithColumn("Attempts").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("DeliveredAt").AsDateTime().Nullable()
                .WithColumn("LastError").AsString(2048).Nullable();
            Create.Index().OnTable("EpisodeVersionNotificationDeliveries").OnColumn("Kind").Ascending().OnColumn("CompletionId").Ascending().OnColumn("NotificationId").Ascending().WithOptions().Unique();
        }
    }
}
