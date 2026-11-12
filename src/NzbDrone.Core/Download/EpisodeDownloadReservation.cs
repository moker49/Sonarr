using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Download
{
    public enum EpisodeDownloadReservationState
    {
        Prepared = 0,
        Dispatching = 1,
        Submitted = 2,
        Uncertain = 3,
        Failed = 4,
        Imported = 5
    }

    public class EpisodeDownloadReservation : ModelBase
    {
        public int SeriesId { get; set; }
        public int SeriesVersionId { get; set; }
        public string ReleaseKey { get; set; }
        public string OriginalReleaseKey { get; set; }
        public DateTime CreatedAt { get; set; }
        public EpisodeDownloadReservationState State { get; set; }
        public int? DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public string NormalizedDownloadId { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string Note { get; set; }
    }

    public class EpisodeDownloadReservationItem : ModelBase
    {
        public int ReservationId { get; set; }
        public int SeriesVersionId { get; set; }
        public int EpisodeId { get; set; }
    }

    public class EpisodeDownloadReservationKey : ModelBase
    {
        public int ReservationId { get; set; }
        public int SeriesVersionId { get; set; }
        public string ReleaseKey { get; set; }
    }
}
