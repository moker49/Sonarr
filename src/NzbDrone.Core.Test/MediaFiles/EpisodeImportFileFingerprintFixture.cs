using System;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpisodeImportFileFingerprintFixture : CoreTest<EpisodeImportFileFingerprintService>
    {
        [Test]
        public void records_sha256_and_size_from_file_content()
        {
            var path = GetTempFilePath();
            File.WriteAllText(path, "abc");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileSize(path)).Returns(3);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(path)).Returns(() => File.OpenRead(path));

            var result = Subject.Read(path);

            result.Size.Should().Be(3);
            result.Hash.Should().Be("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
        }

        [Test]
        public void rejects_a_file_whose_size_changes_during_hashing()
        {
            var path = GetTempFilePath();
            File.WriteAllText(path, "abc");
            Mocker.GetMock<IDiskProvider>().SetupSequence(d => d.GetFileSize(path)).Returns(3).Returns(4);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(path)).Returns(() => File.OpenRead(path));

            Action read = () => Subject.Read(path);

            read.Should().Throw<InvalidOperationException>();
        }
    }
}
