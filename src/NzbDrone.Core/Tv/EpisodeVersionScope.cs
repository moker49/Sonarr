namespace NzbDrone.Core.Tv
{
    public static class EpisodeVersionScope
    {
        // An unknown version can overlap either version and must be handled
        // conservatively until its ownership is reconciled.
        public static bool MayOverlap(int? first, int? second)
        {
            return !first.HasValue || !second.HasValue || first.Value == second.Value;
        }
    }
}
