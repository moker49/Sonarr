using System;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles.EpisodeImport
{
    public interface IEpisodeVersionFileImportService
    {
        EpisodeFile Import(LocalEpisode local, bool newDownload, DownloadClientItem item, ImportMode mode);
    }

    public class EpisodeVersionFileImportService : IEpisodeVersionFileImportService
    {
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IEpisodeVersionImportWorkflow _workflow;
        private readonly IEpisodeImportVersionContext _context;
        private readonly IEpisodeVersionDestinationPlanner _destinations;
        private readonly ITrackedDownloadService _tracked;
        private readonly ISeriesService _series;
        private readonly IEpisodeService _episodes;
        private readonly ICustomFormatCalculationService _formats;
        private readonly IMakeImportDecision _decisions;
        private readonly IConfigService _config;
        private readonly IDiskProvider _disk;
        private readonly IRootFolderService _roots;

        public EpisodeVersionFileImportService(IEpisodeDownloadReservationService reservations, IEpisodeVersionImportIntentService intents, IEpisodeVersionImportWorkflow workflow, IEpisodeImportVersionContext context, IEpisodeVersionDestinationPlanner destinations, ITrackedDownloadService tracked, ISeriesService series, IEpisodeService episodes, ICustomFormatCalculationService formats, IMakeImportDecision decisions, IConfigService config, IDiskProvider disk, IRootFolderService roots)
        {
            _reservations = reservations;
            _intents = intents;
            _workflow = workflow;
            _context = context;
            _destinations = destinations;
            _tracked = tracked;
            _series = series;
            _episodes = episodes;
            _formats = formats;
            _decisions = decisions;
            _config = config;
            _disk = disk;
            _roots = roots;
        }

        public EpisodeFile Import(LocalEpisode local, bool newDownload, DownloadClientItem item, ImportMode mode)
        {
            if (local?.Series == null || !local.SeriesVersionId.HasValue || !newDownload || item?.DownloadClientInfo?.Id <= 0 || item?.DownloadClientInfo == null || string.IsNullOrWhiteSpace(item.DownloadId) || !Enum.IsDefined(typeof(ImportMode), mode))
            {
                throw new InvalidOperationException("A new download with an explicit version and bound client is required.");
            }
            if (_config.UseScriptImport || local.ScriptImported)
            {
                throw new InvalidOperationException("Script imports cannot manage versioned episode files yet.");
            }

            lock (EpisodeDownloadSynchronization.For(item.DownloadClientInfo.Id, item.DownloadId))
            {
                var reservation = _reservations.FindByClientDownload(item.DownloadClientInfo.Id, item.DownloadId);
                if (reservation == null || reservation.SeriesId != local.Series.Id || reservation.SeriesVersionId != local.SeriesVersionId ||
                    (reservation.State != EpisodeDownloadReservationState.Submitted && reservation.State != EpisodeDownloadReservationState.Imported) ||
                    local.Episodes == null || local.Episodes.Count == 0 || local.Episodes.Any(e => e == null || e.Id <= 0) || local.Episodes.Select(e => e.Id).Distinct().Count() != local.Episodes.Count)
                {
                    throw new InvalidOperationException("The import does not match the download's version reservation.");
                }

                var ids = local.Episodes.Select(e => e.Id).ToArray();
                var source = Path.GetFullPath(local.Path);
                var copyOnly = mode == ImportMode.Copy || (mode == ImportMode.Auto && !item.CanMoveFiles);
                var matches = _intents.GetForSeries(reservation.SeriesId).Where(i => i.ReservationId == reservation.Id && i.SeriesVersionId == reservation.SeriesVersionId && i.State != EpisodeVersionImportIntentState.Cancelled && PathEqualityComparer.Instance.Equals(i.SourcePath, source)).ToList();
                if (matches.Count > 1)
                {
                    throw new InvalidOperationException("Multiple import intents match this source; reconcile them before retrying.");
                }

                var currentSeries = _series.GetSeries(reservation.SeriesId);
                if (matches.Count == 1)
                {
                    var existing = matches[0];
                    if (!_intents.GetEpisodeIds(existing.Id).ToHashSet().SetEquals(ids) || !currentSeries.Path.IsParentPath(existing.DestinationPath))
                    {
                        throw new InvalidOperationException("The import's episode coverage or destination changed.");
                    }
                    if (existing.State == EpisodeVersionImportIntentState.Prepared)
                    {
                        // No transfer has begun. Re-run all final checks rather
                        // than trusting a decision made before an interruption.
                        _intents.CancelPrepared(existing.Id);
                    }
                    else
                    {
                        var metadata = existing.State == EpisodeVersionImportIntentState.Registered || existing.State == EpisodeVersionImportIntentState.Completed ? null : Metadata(local, currentSeries, existing.DestinationPath, existing.SourceSize ?? local.Size);
                        return _workflow.Resume(reservation.SeriesId, existing.Id, copyOnly, metadata);
                    }
                }

                if (reservation.State != EpisodeDownloadReservationState.Submitted)
                {
                    throw new InvalidOperationException("The download is already complete and has no matching import intent.");
                }
                local.Series = currentSeries;
                local.Episodes = _episodes.GetEpisodes(ids);
                if (!local.Episodes.Select(e => e.Id).ToHashSet().SetEquals(ids))
                {
                    throw new InvalidOperationException("The import's episode coverage changed.");
                }
                _context.Apply(local, item, _tracked.Find(item.DownloadId));
                if (local.SeriesVersionId != reservation.SeriesVersionId || local.Episodes.Any(e => e.EpisodeFileId != 0))
                {
                    throw new InvalidOperationException("Only empty version episode slots can be imported through this path.");
                }
                local.Size = _disk.GetFileSize(source);
                local.CustomFormats = _formats.ParseCustomFormat(local);
                local.CustomFormatScore = local.Series.QualityProfile.Value.CalculateCustomFormatScore(local.CustomFormats);
                var decision = _decisions.GetDecision(local, item);
                if (!decision.Approved)
                {
                    throw new InvalidOperationException("Final import validation rejected the file: " + string.Join("; ", decision.Rejections.Select(r => r.Message)));
                }

                var destination = _destinations.Plan(local);
                var root = _roots.GetBestRootFolderPath(currentSeries.Path);
                if (string.IsNullOrWhiteSpace(root) || !_disk.FolderExists(root))
                {
                    throw new InvalidOperationException("The series root folder is unavailable.");
                }
                var file = Metadata(local, currentSeries, destination, local.Size);
                var importId = _intents.Prepare(reservation.Id, item.DownloadClientInfo.Id, item.DownloadId, ids, source, destination);
                _disk.CreateFolder(Path.GetDirectoryName(destination));
                return _workflow.Resume(reservation.SeriesId, importId, copyOnly, file);
            }
        }

        private static EpisodeFile Metadata(LocalEpisode local, Series series, string destination, long size)
        {
            return new EpisodeFile { SeriesId = series.Id, SeasonNumber = local.SeasonNumber, RelativePath = series.Path.GetRelativePath(destination), Path = destination, Size = size, DateAdded = DateTime.UtcNow, OriginalFilePath = local.Path, SceneName = local.SceneName, Quality = local.Quality, Languages = local.Languages, MediaInfo = local.MediaInfo, ReleaseGroup = local.ReleaseGroup, ReleaseHash = local.ReleaseHash, ReleaseType = local.ReleaseType, IndexerFlags = local.IndexerFlags };
        }
    }
}
