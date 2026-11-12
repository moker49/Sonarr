using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public enum EpisodeVersionImportIntentState
    {
        Prepared = 0,
        Cancelled = 1,
        Moving = 2,
        Moved = 3,
        Uncertain = 4,
        Registered = 5
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
        public long? SourceSize { get; set; }
        public string SourceHash { get; set; }
        public bool? CopyOnly { get; set; }
        public string Note { get; set; }
        public int? RegisteredFileId { get; set; }
    }
}
