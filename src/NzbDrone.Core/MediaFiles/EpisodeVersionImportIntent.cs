using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public enum EpisodeVersionImportIntentState
    {
        Prepared = 0,
        Cancelled = 1
    }

    public class EpisodeVersionImportIntent : ModelBase
    {
        public int SeriesId { get; set; }
        public int SeriesVersionId { get; set; }
        public int ReservationId { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public EpisodeVersionImportIntentState State { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
