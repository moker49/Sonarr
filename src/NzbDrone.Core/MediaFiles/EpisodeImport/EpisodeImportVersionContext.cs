using System;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.EpisodeImport
{
    public interface IEpisodeImportVersionContext
    {
        void Apply(LocalEpisode localEpisode, DownloadClientItem item, TrackedDownload tracked);
    }

    public class EpisodeImportVersionContext : IEpisodeImportVersionContext
    {
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly IEpisodeVersionDecisionContext _versions;

        public EpisodeImportVersionContext(IEpisodeDownloadReservationService reservations, IEpisodeVersionDecisionContext versions)
        {
            _reservations = reservations;
            _versions = versions;
        }

        public void Apply(LocalEpisode localEpisode, DownloadClientItem item, TrackedDownload tracked)
        {
            var clientId = item?.DownloadClientInfo?.Id;
            if (!clientId.HasValue || item.DownloadId.IsNullOrWhiteSpace())
            {
                if (tracked?.SeriesVersionId != null || tracked?.EpisodeDownloadReservationId != null || tracked?.HasUnresolvedReservation == true)
                {
                    throw new InvalidOperationException("The download client identity is unavailable.");
                }

                return;
            }

            if (tracked != null && tracked.DownloadClient != clientId.Value)
            {
                throw new InvalidOperationException("The tracked download belongs to another client.");
            }

            var reservation = _reservations.FindByClientDownload(clientId.Value, item.DownloadId);
            if (reservation == null)
            {
                if (tracked?.SeriesVersionId != null || tracked?.EpisodeDownloadReservationId != null || tracked?.HasUnresolvedReservation == true || _reservations.HasUnboundForClient(clientId.Value))
                {
                    throw new InvalidOperationException("The download's version reservation could not be resolved.");
                }

                return;
            }

            if (tracked == null || tracked.DownloadClient != clientId.Value || tracked.SeriesVersionId != reservation.SeriesVersionId ||
                tracked.EpisodeDownloadReservationId != reservation.Id || reservation.SeriesId != localEpisode.Series?.Id ||
                reservation.State != EpisodeDownloadReservationState.Submitted)
            {
                throw new InvalidOperationException("The tracked download and its version reservation do not agree.");
            }

            var reservedEpisodes = _reservations.GetEpisodeIds(reservation.Id).ToHashSet();
            if (localEpisode.Episodes == null || localEpisode.Episodes.Count == 0 ||
                localEpisode.Episodes.Any(e => e == null || !reservedEpisodes.Contains(e.Id)))
            {
                throw new InvalidOperationException("The file contains episodes outside the download's version reservation.");
            }

            var resolved = _versions.ForVersion(new RemoteEpisode { Series = localEpisode.Series, Episodes = localEpisode.Episodes }, reservation.SeriesVersionId);
            if (resolved?.Series == null || resolved.Episodes == null || resolved.SeriesVersionId != reservation.SeriesVersionId)
            {
                throw new InvalidOperationException("The version context could not be restored.");
            }

            localEpisode.Series = resolved.Series;
            localEpisode.Episodes = resolved.Episodes;
            localEpisode.SeriesVersionId = reservation.SeriesVersionId;
        }
    }
}
