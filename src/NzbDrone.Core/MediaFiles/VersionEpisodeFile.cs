using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public class VersionEpisodeFile : ModelBase
    {
        public int SeriesVersionId { get; set; }
        public int EpisodeFileId { get; set; }
    }
}
