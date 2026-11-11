using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Profiles.Qualities;
using Sonarr.Api.V3.Profiles.Quality;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class CompositeProfileControllerFixture
    {
        [Test]
        public void save_rejects_missing_versions_as_bad_request()
        {
            var controller = new CompositeProfileController(Mock.Of<ICompositeProfileDraftService>(), Mock.Of<IQualityProfileService>());
            var error = Assert.Throws<NzbDroneClientException>(() => controller.Save(1, new CompositeProfileResource()));
            error.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void save_reports_conflicting_draft_changes()
        {
            var drafts = new Mock<ICompositeProfileDraftService>();
            drafts.Setup(d => d.Save(1, It.IsAny<List<CompositeProfileVersion>>())).Throws(new InvalidOperationException("Profile in use"));
            var controller = new CompositeProfileController(drafts.Object, Mock.Of<IQualityProfileService>());
            var error = Assert.Throws<NzbDroneClientException>(() => controller.Save(1, new CompositeProfileResource { Versions = new List<CompositeProfileVersion>() }));
            error.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Test]
        public void quality_profile_list_marks_drafts()
        {
            var profiles = new Mock<IQualityProfileService>();
            profiles.Setup(p => p.All()).Returns(new List<QualityProfile>
            {
                new QualityProfile { Id = 1, Items = new List<QualityProfileQualityItem>() },
                new QualityProfile { Id = 2, Items = new List<QualityProfileQualityItem>() }
            });
            var drafts = new Mock<ICompositeProfileDraftService>();
            drafts.Setup(d => d.GetProfileIds()).Returns(new HashSet<int> { 2 });
            var controller = new QualityProfileController(profiles.Object, Mock.Of<NzbDrone.Core.CustomFormats.ICustomFormatService>(), drafts.Object);

            var result = controller.GetAll();
            result[0].IsComposite.Should().BeFalse();
            result[1].IsComposite.Should().BeTrue();
        }
    }
}
