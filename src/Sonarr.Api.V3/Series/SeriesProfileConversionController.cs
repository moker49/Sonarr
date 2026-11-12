using System;
using System.Collections.Generic;
using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Tv;
using Sonarr.Http;

namespace Sonarr.Api.V3.Series
{
    [V3ApiController("series/{seriesId:int}/profileconversion")]
    public class SeriesProfileConversionController : Controller
    {
        private readonly ISeriesVersionAssignmentPlanner _planner;
        private readonly ISeriesVersionAssignmentService _assignments;

        public SeriesProfileConversionController(ISeriesVersionAssignmentPlanner planner, ISeriesVersionAssignmentService assignments)
        {
            _planner = planner;
            _assignments = assignments;
        }

        [HttpPost("preview")]
        public SeriesVersionAssignmentPlan Preview(int seriesId, [FromBody] SeriesProfileConversionResource resource)
        {
            return Execute(resource, () => _planner.Preview(seriesId, resource.CompositeProfileId, resource.ExistingFileVersionId));
        }

        [HttpPost]
        public List<SeriesVersion> Apply(int seriesId, [FromBody] SeriesProfileConversionResource resource)
        {
            return Execute(resource, () => _assignments.Apply(seriesId, resource.CompositeProfileId, resource.ExistingFileVersionId, resource.SnapshotToken));
        }

        private static T Execute<T>(SeriesProfileConversionResource resource, Func<T> operation)
        {
            if (resource == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Conversion details are required.");
            }
            try
            {
                return operation();
            }
            catch (ValidationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message, ex);
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
    }

    public class SeriesProfileConversionResource
    {
        public int CompositeProfileId { get; set; }
        public int? ExistingFileVersionId { get; set; }
        public string SnapshotToken { get; set; }
    }
}
