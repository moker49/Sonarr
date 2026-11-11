using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V3.Series
{
    [V3ApiController("series/{seriesId:int}/downloadreservation")]
    public class EpisodeDownloadReservationsController : Controller
    {
        private readonly ISeriesService _series;
        private readonly IEpisodeDownloadReservationService _reservations;
        private readonly IEpisodeReservationRecoveryService _recovery;

        public EpisodeDownloadReservationsController(ISeriesService series, IEpisodeDownloadReservationService reservations, IEpisodeReservationRecoveryService recovery)
        {
            _series = series;
            _reservations = reservations;
            _recovery = recovery;
        }

        [HttpGet]
        public List<EpisodeDownloadReservationResource> Get(int seriesId)
        {
            _series.GetSeries(seriesId);
            return _reservations.ForSeries(seriesId).Select(ToResource).ToList();
        }

        [HttpPost("{reservationId:int}/bind")]
        public EpisodeDownloadReservationResource Bind(int seriesId, int reservationId, [FromBody] EpisodeReservationBindResource resource)
        {
            if (resource == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "A client, download ID and explanation are required.");
            }

            try
            {
                _recovery.BindTrackedDownload(seriesId, reservationId, resource.DownloadClientId, resource.DownloadId, resource.Note);
                return ToResource(_reservations.ForSeries(seriesId).Single(r => r.Id == reservationId));
            }
            catch (ArgumentException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message, ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message, ex);
            }
        }

        private EpisodeDownloadReservationResource ToResource(EpisodeDownloadReservation reservation)
        {
            return new EpisodeDownloadReservationResource
            {
                Id = reservation.Id,
                SeriesId = reservation.SeriesId,
                SeriesVersionId = reservation.SeriesVersionId,
                EpisodeIds = _reservations.GetEpisodeIds(reservation.Id),
                State = reservation.State,
                DownloadClientId = reservation.DownloadClientId,
                DownloadId = reservation.DownloadId,
                CreatedAt = reservation.CreatedAt,
                UpdatedAt = reservation.UpdatedAt,
                Note = reservation.Note
            };
        }
    }

    public class EpisodeDownloadReservationResource
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public int SeriesVersionId { get; set; }
        public List<int> EpisodeIds { get; set; }
        public EpisodeDownloadReservationState State { get; set; }
        public int? DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string Note { get; set; }
    }

    public class EpisodeReservationBindResource
    {
        public int DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public string Note { get; set; }
    }
}
