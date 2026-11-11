using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Tv
{
    public class SeriesVersion : ModelBase
    {
        public int SeriesId { get; set; }
        public int CompositeProfileVersionId { get; set; }
        public bool Retired { get; set; }
    }
}
