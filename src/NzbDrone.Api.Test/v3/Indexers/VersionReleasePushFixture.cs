using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using Sonarr.Api.V3.Indexers;

namespace NzbDrone.Api.Test.v3.Indexers
{
    [TestFixture]
    public class VersionReleasePushFixture
    {
        private class PushController : ReleasePushController
        {
            public PushController(IMakeDownloadDecision maker, IProcessDownloadDecisions processor)
                : base(maker, processor, Mock.Of<IIndexerFactory>(), Mock.Of<IDownloadClientFactory>(), Mock.Of<IQualityProfileService>(), LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
                Request.Method = "POST";
            }

            protected override List<ReleaseResource> MapDecisions(IEnumerable<DownloadDecision> decisions)
            {
                return decisions.Select(d => new ReleaseResource { SeriesVersionId = d.RemoteEpisode.SeriesVersionId }).ToList();
            }
        }

        private static ReleaseResource Resource() => new ReleaseResource
        {
            Title = "Series.S01E01.720p",
            DownloadUrl = "https://example.invalid/release.nzb",
            PublishDate = DateTime.UtcNow,
            Protocol = DownloadProtocol.Usenet,
            DownloadClientId = 12
        };

        [Test]
        public void pushes_process_both_child_decisions_with_the_selected_client()
        {
            var decisions = new List<DownloadDecision>
            {
                new DownloadDecision(new RemoteEpisode { SeriesVersionId = 7, ParsedEpisodeInfo = new ParsedEpisodeInfo() }),
                new DownloadDecision(new RemoteEpisode { SeriesVersionId = 8, ParsedEpisodeInfo = new ParsedEpisodeInfo() })
            };
            var maker = new Mock<IMakeDownloadDecision>();
            maker.Setup(m => m.GetRssDecision(It.IsAny<List<ReleaseInfo>>(), true)).Returns(decisions);
            var processor = new Mock<IProcessDownloadDecisions>();
            processor.Setup(p => p.ProcessDecision(It.IsAny<DownloadDecision>(), 12)).ReturnsAsync(ProcessedDecisionResult.Grabbed);

            var response = new PushController(maker.Object, processor.Object).Create(Resource());

            response.Value.Select(r => r.SeriesVersionId).Should().Equal(7, 8);
            processor.Verify(p => p.ProcessDecision(decisions[0], 12), Times.Once());
            processor.Verify(p => p.ProcessDecision(decisions[1], 12), Times.Once());
        }

        [Test]
        public void malformed_expanded_decision_set_is_rejected_before_dispatch()
        {
            var maker = new Mock<IMakeDownloadDecision>();
            maker.Setup(m => m.GetRssDecision(It.IsAny<List<ReleaseInfo>>(), true)).Returns(new List<DownloadDecision>
            {
                new DownloadDecision(new RemoteEpisode { SeriesVersionId = 7, ParsedEpisodeInfo = new ParsedEpisodeInfo() }),
                new DownloadDecision(new RemoteEpisode { SeriesVersionId = 8 })
            });
            var processor = new Mock<IProcessDownloadDecisions>();
            Action push = () => new PushController(maker.Object, processor.Object).Create(Resource());
            push.Should().Throw<ValidationException>();
            processor.Verify(p => p.ProcessDecision(It.IsAny<DownloadDecision>(), It.IsAny<int?>()), Times.Never());
        }
    }
}
