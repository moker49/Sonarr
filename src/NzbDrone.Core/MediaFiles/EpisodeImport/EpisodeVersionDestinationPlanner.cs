using System;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.MediaFiles.EpisodeImport
{
    public interface IEpisodeVersionDestinationPlanner
    {
        string Plan(LocalEpisode localEpisode);
    }

    // A read-only preview; import must revalidate the destination under its
    // filesystem/database synchronization before any file is moved.
    public class EpisodeVersionDestinationPlanner : IEpisodeVersionDestinationPlanner
    {
        private readonly IBuildFileNames _names;
        private readonly IMediaFileService _files;
        private readonly IDiskProvider _disk;

        public EpisodeVersionDestinationPlanner(IBuildFileNames names, IMediaFileService files, IDiskProvider disk)
        {
            _names = names;
            _files = files;
            _disk = disk;
        }

        public string Plan(LocalEpisode local)
        {
            if (local?.Series == null || local.SeriesVersionId == null || !CompositeProfileValidator.ValidLabel(local.SeriesVersionLabel) ||
                local.Episodes == null || local.Episodes.Count == 0 || local.Episodes.Any(e => e == null || e.SeriesId != local.Series.Id))
            {
                throw new InvalidOperationException("A valid version and its episodes are required to plan an import.");
            }

            var file = new EpisodeFile
            {
                SeriesId = local.Series.Id,
                Series = local.Series,
                Episodes = local.Episodes,
                Path = local.Path,
                Quality = local.Quality,
                MediaInfo = local.MediaInfo,
                SceneName = local.SceneName,
                ReleaseGroup = local.ReleaseGroup,
                ReleaseHash = local.ReleaseHash,
                Languages = local.Languages,
                ReleaseType = local.ReleaseType,
                IndexerFlags = local.IndexerFlags
            };
            var destination = _names.BuildVersionFilePath(local.Episodes, local.Series, file, Path.GetExtension(local.Path), local.SeriesVersionLabel, null, local.CustomFormats);
            if (destination.IsNullOrWhiteSpace() || !local.Series.Path.IsParentPath(destination))
            {
                throw new InvalidOperationException("The planned version destination is outside the series folder.");
            }

            var currentIds = local.Episodes.Where(e => e.EpisodeFileId > 0).Select(e => e.EpisodeFileId).ToHashSet();
            var existing = _files.GetFilesBySeries(local.Series.Id)
                .Where(f => PathEqualityComparer.Instance.Equals(Path.Combine(local.Series.Path, f.RelativePath), destination))
                .ToList();
            if (existing.Count > 1 || existing.Any(f => !currentIds.Contains(f.Id)) || (existing.Count == 0 && _disk.FileExists(destination)))
            {
                throw new InvalidOperationException("The planned version destination is already occupied by another file.");
            }

            return destination;
        }
    }
}
