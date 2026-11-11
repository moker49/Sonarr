using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class SeriesVersionFoundationFixture : DbTest
    {
        [Test]
        public void shared_file_can_cover_multiple_episodes_but_only_one_version()
        {
            var files = Mocker.Resolve<BasicRepository<VersionEpisodeFile>>();
            var assignments = Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>();
            var file = files.Insert(new VersionEpisodeFile { SeriesVersionId = 1, EpisodeFileId = 25 });

            assignments.Insert(new EpisodeVersionAssignment { SeriesVersionId = 1, EpisodeId = 10, VersionEpisodeFileId = file.Id });
            assignments.Insert(new EpisodeVersionAssignment { SeriesVersionId = 1, EpisodeId = 11, VersionEpisodeFileId = file.Id });
            assignments.All().Should().HaveCount(2);

            Action duplicateOwner = () => files.Insert(new VersionEpisodeFile { SeriesVersionId = 2, EpisodeFileId = 25 });
            duplicateOwner.Should().Throw<Exception>();
        }

        [Test]
        public void episode_can_have_one_assignment_per_series_version()
        {
            var assignments = Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>();
            assignments.Insert(new EpisodeVersionAssignment { SeriesVersionId = 1, EpisodeId = 10 });
            assignments.Insert(new EpisodeVersionAssignment { SeriesVersionId = 2, EpisodeId = 10 });

            Action duplicate = () => assignments.Insert(new EpisodeVersionAssignment { SeriesVersionId = 1, EpisodeId = 10 });
            duplicate.Should().Throw<Exception>();
            assignments.All().Should().HaveCount(2);
        }
    }
}
