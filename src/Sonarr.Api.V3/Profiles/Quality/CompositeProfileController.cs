using System;
using System.Collections.Generic;
using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Profiles.Qualities;
using Sonarr.Http;

namespace Sonarr.Api.V3.Profiles.Quality
{
    [V3ApiController("qualityprofile/{profileId:int}/composite")]
    public class CompositeProfileController : Controller
    {
        private readonly ICompositeProfileDraftService _drafts;
        private readonly IQualityProfileService _profiles;

        public CompositeProfileController(ICompositeProfileDraftService drafts, IQualityProfileService profiles)
        {
            _drafts = drafts;
            _profiles = profiles;
        }

        [HttpGet]
        public CompositeProfileResource Get(int profileId)
        {
            _profiles.Get(profileId);
            return new CompositeProfileResource { ProfileId = profileId, Versions = _drafts.Get(profileId) };
        }

        [HttpPut]
        public CompositeProfileResource Save(int profileId, [FromBody] CompositeProfileResource resource)
        {
            if (resource?.Versions == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Two version definitions are required.");
            }

            try
            {
                return new CompositeProfileResource { ProfileId = profileId, Versions = _drafts.Save(profileId, resource.Versions) };
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

        [HttpDelete]
        public IActionResult Delete(int profileId)
        {
            try
            {
                _drafts.Delete(profileId);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message, ex);
            }
        }
    }

    public class CompositeProfileResource
    {
        public int ProfileId { get; set; }
        public bool IsDraft => true;
        public List<CompositeProfileVersion> Versions { get; set; }
    }
}
