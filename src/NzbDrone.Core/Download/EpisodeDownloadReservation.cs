using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Download
{
    public class EpisodeDownloadReservation : ModelBase
    {
        public int SeriesId { get; set; }
        public int SeriesVersionId { get; set; }
        public string ReleaseKey { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class EpisodeDownloadReservationItem : ModelBase
    {
        public int ReservationId { get; set; }
        public int SeriesVersionId { get; set; }
        public int EpisodeId { get; set; }
    }
}
