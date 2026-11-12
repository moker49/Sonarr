using System;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class CompositeProfilePolicyFixture : DbTest<CompositeProfilePolicy, CompositeProfileVersion>
    {
        [SetUp]
        public void Setup()
        {
            Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
        }

        [Test]
        public void ordinary_profiles_remain_assignable_and_unrelated_profiles_deletable()
        {
            Action assign = () => Subject.EnsureAssignable(new[] { 1, 2, 3 });
            Action delete = () => Subject.EnsureDeletable(3);
            assign.Should().NotThrow();
            delete.Should().NotThrow();
        }

        [Test]
        public void legacy_operation_checks_authoritative_series_assignment_and_prior_versions()
        {
            var series = Db.Insert(FizzWare.NBuilder.Builder<NzbDrone.Core.Tv.Series>.CreateNew().With(s => s.QualityProfileId = 1).BuildNew());
            Action ordinary = () => Subject.EnsureSeriesOrdinary(series.Id);
            ordinary.Should().NotThrow();

            var repository = Mocker.Resolve<NzbDrone.Core.Datastore.BasicRepository<NzbDrone.Core.Tv.Series>>();
            series.QualityProfileId = 10;
            repository.Update(series);
            ordinary.Should().Throw<NzbDroneClientException>();

            series.QualityProfileId = 1;
            repository.Update(series);
            Db.Insert(new NzbDrone.Core.Tv.SeriesVersion { SeriesId = series.Id, CompositeProfileVersionId = 1, Retired = true });
            ordinary.Should().Throw<NzbDroneClientException>();
        }

        [Test]
        public void drafts_cannot_be_assigned_even_in_a_mixed_batch()
        {
            Action assign = () => Subject.EnsureAssignable(new[] { 1, 10 });
            assign.Should().Throw<NzbDroneClientException>();
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(10)]
        public void protects_parent_and_child_profiles_from_deletion(int profileId)
        {
            Action delete = () => Subject.EnsureDeletable(profileId);
            delete.Should().Throw<NzbDroneClientException>();
        }
    }
}
