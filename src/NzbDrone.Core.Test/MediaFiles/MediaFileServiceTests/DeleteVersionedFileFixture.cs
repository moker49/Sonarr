using System;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.MediaFileServiceTests
{
    [TestFixture]
    public class DeleteVersionedFileFixture : CoreTest<MediaFileService>
    {
        [Test]
        public void refuses_to_delete_a_version_owned_file_from_the_database()
        {
            var file = new EpisodeFile { Id = 42 };
            Mocker.GetMock<IVersionEpisodeFileAssignmentService>().Setup(v => v.IsOwnedFile(42)).Returns(true);

            Assert.Throws<InvalidOperationException>(() => Subject.Delete(file, DeleteMediaFileReason.ManualOverride));

            Mocker.GetMock<IMediaFileRepository>().Verify(r => r.Delete(It.IsAny<EpisodeFile>()), Times.Never());
        }
    }
}
