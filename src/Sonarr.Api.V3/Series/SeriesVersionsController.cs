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
    [V3ApiController("series/{seriesId:int}/versions")]
    public class SeriesVersionsController : Controller
    {
        private readonly ISeriesVersionStatusService _statuses;

        public SeriesVersionsController(ISeriesVersionStatusService statuses)
        {
            _statuses = statuses;
        }

        [HttpGet]
        public List<SeriesVersionStatus> Get(int seriesId)
        {
            try
            {
                return _statuses.Get(seriesId);
            }
            catch (ValidationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message, ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message, ex);
            }
        }
    }
}
