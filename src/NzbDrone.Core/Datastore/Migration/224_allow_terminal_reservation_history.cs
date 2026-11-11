using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(224)]
    public class allow_terminal_reservation_history : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Active release identity is enforced by EpisodeDownloadReservationKeys.
            // The reservation row can then remain as history after its keys are freed.
            Delete.Index("IX_EpisodeDownloadReservations_SeriesVersionId_ReleaseKey").OnTable("EpisodeDownloadReservations");
        }
    }
}
