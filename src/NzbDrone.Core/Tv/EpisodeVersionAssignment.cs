using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Tv
{
    public class EpisodeVersionAssignment : ModelBase
    {
        public int SeriesVersionId { get; set; }
        public int EpisodeId { get; set; }
        public int? VersionEpisodeFileId { get; set; }
    }
}
