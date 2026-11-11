using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ImportLists
{
    [TestFixture]
    public class CompositeImportListGuardFixture : CoreTest<ImportListFactory>
    {
        [Test]
        public void rejects_bulk_updates_before_writing_any_list()
        {
            var lists = new List<ImportListDefinition> { new ImportListDefinition { QualityProfileId = 1 }, new ImportListDefinition { QualityProfileId = 2 } };
            Mocker.GetMock<ICompositeProfilePolicy>().Setup(p => p.EnsureAssignable(It.IsAny<IEnumerable<int>>())).Throws<InvalidOperationException>();

            Assert.Throws<InvalidOperationException>(() => Subject.Update(lists));
            Mocker.GetMock<IImportListRepository>().Verify(r => r.UpdateMany(It.IsAny<IList<ImportListDefinition>>()), Times.Never());
        }

        [Test]
        public void rejects_new_list_before_inserting()
        {
            Mocker.GetMock<ICompositeProfilePolicy>().Setup(p => p.EnsureAssignable(It.IsAny<IEnumerable<int>>())).Throws<InvalidOperationException>();

            Assert.Throws<InvalidOperationException>(() => Subject.Create(new ImportListDefinition { QualityProfileId = 2 }));
            Mocker.GetMock<IImportListRepository>().Verify(r => r.Insert(It.IsAny<ImportListDefinition>()), Times.Never());
        }
    }
}
