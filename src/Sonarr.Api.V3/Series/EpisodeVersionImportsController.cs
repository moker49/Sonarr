using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V3.Series
{
    [V3ApiController("series/{seriesId:int}/versionimport")]
    public class EpisodeVersionImportsController : Controller
    {
        private readonly ISeriesService _series;
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IEpisodeVersionImportInspectionService _inspections;

        public EpisodeVersionImportsController(ISeriesService series, IEpisodeVersionImportIntentService intents, IEpisodeVersionImportInspectionService inspections)
        {
            _series = series;
            _intents = intents;
            _inspections = inspections;
        }

        [HttpGet]
        public List<EpisodeVersionImportResource> Get(int seriesId)
        {
            _series.GetSeries(seriesId);
            return _intents.GetActive(seriesId).Select(ToResource).ToList();
        }

        [HttpGet("history")]
        public List<EpisodeVersionImportResource> History(int seriesId)
        {
            _series.GetSeries(seriesId);
            return _intents.GetForSeries(seriesId).Select(ToResource).ToList();
        }

        // Hashing large video files is deliberately opt-in, not part of listing.
        [HttpGet("{importId:int}/inspection")]
        public EpisodeVersionImportInspectionResource Inspect(int seriesId, int importId)
        {
            _series.GetSeries(seriesId);
            try
            {
                var inspection = _inspections.Inspect(seriesId, importId);
                return new EpisodeVersionImportInspectionResource
                {
                    Import = ToResource(inspection.Intent),
                    Observation = inspection.Observation,
                    Source = inspection.Source,
                    Destination = inspection.Destination
                };
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message, ex);
            }
        }

        [HttpPost("{importId:int}/confirm-moved")]
        public EpisodeVersionImportResource ConfirmMoved(int seriesId, int importId, [FromBody] EpisodeVersionImportRecoveryResource resource)
        {
            _series.GetSeries(seriesId);
            if (resource == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "A recovery explanation is required.");
            }

            try
            {
                _intents.RecoverMoved(seriesId, importId, resource.Note);
                return ToResource(_intents.GetActive(seriesId).Single(i => i.Id == importId));
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

        private EpisodeVersionImportResource ToResource(EpisodeVersionImportIntent intent)
        {
            return new EpisodeVersionImportResource
            {
                Id = intent.Id,
                SeriesId = intent.SeriesId,
                SeriesVersionId = intent.SeriesVersionId,
                ReservationId = intent.ReservationId,
                RegisteredFileId = intent.RegisteredFileId,
                EpisodeIds = _intents.GetEpisodeIds(intent.Id),
                SourcePath = intent.SourcePath,
                DestinationPath = intent.DestinationPath,
                State = intent.State,
                CopyOnly = intent.CopyOnly,
                SourceSize = intent.SourceSize,
                CreatedAt = intent.CreatedAt,
                UpdatedAt = intent.UpdatedAt,
                Note = intent.Note
            };
        }
    }

    public class EpisodeVersionImportResource
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public int SeriesVersionId { get; set; }
        public int ReservationId { get; set; }
        public int? RegisteredFileId { get; set; }
        public List<int> EpisodeIds { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public EpisodeVersionImportIntentState State { get; set; }
        public bool? CopyOnly { get; set; }
        public long? SourceSize { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Note { get; set; }
    }

    public class EpisodeVersionImportInspectionResource
    {
        public EpisodeVersionImportResource Import { get; set; }
        public EpisodeVersionImportObservation Observation { get; set; }
        public EpisodeVersionImportFileObservation Source { get; set; }
        public EpisodeVersionImportFileObservation Destination { get; set; }
    }

    public class EpisodeVersionImportRecoveryResource
    {
        public string Note { get; set; }
    }
}
