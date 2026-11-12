using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class SeriesVersionAssignmentServiceFixture : DbTest<SeriesVersionAssignmentService, SeriesVersion>
    {
        private Series _series;
        private CompositeProfileVersion _primary;
        private CompositeProfileVersion _secondary;
        private EpisodeFile _file;
        private Episode _first;

        [SetUp]
        public void Setup()
        {
            for (var i = 1; i <= 10; i++)
            {
                Db.Insert(new QualityProfile { Name = "Profile " + i, Items = new List<QualityProfileQualityItem>() });
            }
            _series = Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = 5).With(s => s.Path = @"C:\TV\Show".AsOsAgnostic()).BuildNew());
            _primary = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "HD", Priority = 0, IsPrimary = true });
            _secondary = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "4K", Priority = 1 });
            _file = Db.Insert(new EpisodeFile { SeriesId = _series.Id, RelativePath = "shared.mkv", Size = 123, DateAdded = DateTime.UtcNow, Quality = new QualityModel(), Languages = new List<Language> { Language.English } });
            _first = Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = _file.Id).BuildNew());
            Db.Insert(Builder<Episode>.CreateNew().With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = _file.Id).BuildNew());
            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.GetTrackedDownloads()).Returns(new List<TrackedDownload>());
            Mocker.GetMock<IRootFolderService>().Setup(r => r.GetBestRootFolderPath(_series.Path)).Returns(@"C:\TV".AsOsAgnostic());
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileSize(It.IsAny<string>())).Returns(123);
        }

        private SeriesVersionAssignmentPlan Preview(int definitionId) => Mocker.Resolve<SeriesVersionAssignmentPlanner>().Preview(_series.Id, 10, definitionId);

        [TestCase(true)]
        [TestCase(false)]
        public void converts_shared_files_into_the_explicit_destination_without_moving_files(bool primary)
        {
            var target = primary ? _primary : _secondary;
            var plan = Preview(target.Id);

            var versions = Subject.Apply(_series.Id, 10, target.Id, plan.SnapshotToken);

            versions.Should().HaveCount(2);
            Mocker.Resolve<BasicRepository<Series>>().Get(_series.Id).QualityProfileId.Should().Be(10);
            Mocker.Resolve<BasicRepository<EpisodeVersionAssignment>>().All().Should().HaveCount(4);
            Mocker.Resolve<BasicRepository<VersionEpisodeFile>>().All().Should().ContainSingle(f => f.EpisodeFileId == _file.Id && f.SeriesVersionId == versions.Single(v => v.CompositeProfileVersionId == target.Id).Id);
            Mocker.Resolve<BasicRepository<Episode>>().Get(_first.Id).EpisodeFileId.Should().Be(primary ? _file.Id : 0);
            var resolved = Mocker.Resolve<EpisodeVersionResolver>().Resolve(_first.Id);
            resolved.Should().HaveCount(2).And.ContainSingle(v => v.EpisodeFileId == _file.Id && v.CompositeProfileVersionId == target.Id);
            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.DeleteFile(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void stale_preview_is_rejected_without_partial_assignments()
        {
            var plan = Preview(_primary.Id);
            _file.Size = 456;
            Mocker.Resolve<BasicRepository<EpisodeFile>>().Update(_file);

            Action apply = () => Subject.Apply(_series.Id, 10, _primary.Id, plan.SnapshotToken);

            apply.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
            Mocker.Resolve<BasicRepository<Series>>().Get(_series.Id).QualityProfileId.Should().Be(5);
        }

        [Test]
        public void active_download_or_missing_file_blocks_conversion()
        {
            var plan = Preview(_primary.Id);
            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.GetTrackedDownloads()).Returns(new List<TrackedDownload> { new TrackedDownload { IsTrackable = true, RemoteEpisode = new NzbDrone.Core.Parser.Model.RemoteEpisode { Series = _series } } });
            Action apply = () => Subject.Apply(_series.Id, 10, _primary.Id, plan.SnapshotToken);
            apply.Should().Throw<InvalidOperationException>();
            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.GetTrackedDownloads()).Returns(new List<TrackedDownload>());
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns(false);
            apply.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void durable_reservations_and_unfinished_journals_block_conversion_without_partial_writes(bool reservation)
        {
            var plan = Preview(_primary.Id);
            if (reservation)
            {
                Db.Insert(new EpisodeDownloadReservation { SeriesId = _series.Id, SeriesVersionId = 99, ReleaseKey = "active", CreatedAt = DateTime.UtcNow });
            }
            else
            {
                Db.Insert(new EpisodeVersionImportIntent { SeriesId = _series.Id, SeriesVersionId = 99, ReservationId = 1, SourcePath = "source", DestinationPath = "destination", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            }

            Action apply = () => Subject.Apply(_series.Id, 10, _primary.Id, plan.SnapshotToken);

            apply.Should().Throw<InvalidOperationException>();
            AllStoredModels.Should().BeEmpty();
            Mocker.Resolve<BasicRepository<Series>>().Get(_series.Id).QualityProfileId.Should().Be(5);
        }

        [Test]
        public void database_failure_rolls_back_versions_owners_and_profile_change()
        {
            if (Mocker.Resolve<IMainDatabase>().DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite trigger injects an assignment failure.");
            }
            var plan = Preview(_primary.Id);
            using (var connection = Mocker.Resolve<IMainDatabase>().OpenConnection())
            {
                connection.Execute("CREATE TRIGGER FailConversion BEFORE INSERT ON \"EpisodeVersionAssignments\" BEGIN SELECT RAISE(ABORT, 'Injected failure'); END;");
            }

            Action apply = () => Subject.Apply(_series.Id, 10, _primary.Id, plan.SnapshotToken);

            apply.Should().Throw<Exception>();
            AllStoredModels.Should().BeEmpty();
            Mocker.Resolve<BasicRepository<VersionEpisodeFile>>().All().Should().BeEmpty();
            Mocker.Resolve<BasicRepository<Series>>().Get(_series.Id).QualityProfileId.Should().Be(5);
            Mocker.Resolve<BasicRepository<Episode>>().Get(_first.Id).EpisodeFileId.Should().Be(_file.Id);
        }
    }
}
