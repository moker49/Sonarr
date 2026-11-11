using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.TvTests
{
    [TestFixture]
    public class CompositeSeriesGuardFixture : CoreTest<SeriesService>
    {
        [Test]
        public void bulk_add_checks_all_profiles_before_writing()
        {
            var series = new List<Series> { new Series { QualityProfileId = 1 }, new Series { QualityProfileId = 2 } };
            Mocker.GetMock<ICompositeProfilePolicy>().Setup(p => p.EnsureAssignable(It.IsAny<IEnumerable<int>>())).Throws<InvalidOperationException>();

            Assert.Throws<InvalidOperationException>(() => Subject.AddSeries(series));
            Mocker.GetMock<ISeriesRepository>().Verify(r => r.InsertMany(It.IsAny<IList<Series>>()), Times.Never());
        }

        [Test]
        public void bulk_update_checks_profiles_before_updating_any_series()
        {
            var series = new List<Series> { new Series { QualityProfileId = 1 }, new Series { QualityProfileId = 2 } };
            Mocker.GetMock<ICompositeProfilePolicy>().Setup(p => p.EnsureAssignable(It.IsAny<IEnumerable<int>>())).Throws<InvalidOperationException>();

            Assert.Throws<InvalidOperationException>(() => Subject.UpdateSeries(series, false));
            Mocker.GetMock<ISeriesRepository>().Verify(r => r.UpdateMany(It.IsAny<IList<Series>>()), Times.Never());
        }
    }
}
