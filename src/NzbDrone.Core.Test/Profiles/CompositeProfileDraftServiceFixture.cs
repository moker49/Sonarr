using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class CompositeProfileDraftServiceFixture : DbTest<CompositeProfileDraftService, CompositeProfileVersion>
    {
        private QualityProfile _parent;
        private QualityProfile _first;
        private QualityProfile _second;

        [SetUp]
        public void Setup()
        {
            _parent = CreateProfile("Composite");
            _first = CreateProfile("SDR");
            _second = CreateProfile("HDR");
        }

        private QualityProfile CreateProfile(string name)
        {
            return Db.Insert(new QualityProfile { Name = name, Cutoff = Quality.HDTV720p.Id, Items = Qualities.QualityFixture.GetDefaultQualities(Quality.HDTV720p) });
        }

        private List<CompositeProfileVersion> Definitions()
        {
            return new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { QualityProfileId = _first.Id, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { QualityProfileId = _second.Id, Label = "HDR", Priority = 1 }
            };
        }

        [Test]
        public void saves_and_reorders_without_changing_version_ids()
        {
            var versions = Subject.Save(_parent.Id, Definitions());
            versions.Should().HaveCount(2);
            var firstId = versions.Single(v => v.Label == "SDR").Id;
            var secondId = versions.Single(v => v.Label == "HDR").Id;
            versions[0].Priority = 1;
            versions[1].Priority = 0;

            var saved = Subject.Save(_parent.Id, versions);
            saved.Select(v => v.Label).Should().Equal("HDR", "SDR");
            saved.Single(v => v.Label == "SDR").Id.Should().Be(firstId);
            saved.Single(v => v.Label == "HDR").Id.Should().Be(secondId);
        }

        [Test]
        public void rejects_stale_edits_without_partial_changes()
        {
            var existing = Subject.Save(_parent.Id, Definitions());
            var stale = Definitions();

            Action save = () => Subject.Save(_parent.Id, stale);
            save.Should().Throw<InvalidOperationException>();
            Subject.Get(_parent.Id).Select(v => v.Id).Should().Equal(existing.Select(v => v.Id));
        }

        [Test]
        public void rejects_nested_composites_and_profiles_already_assigned_to_series()
        {
            Subject.Save(_parent.Id, Definitions());
            var nestedParent = CreateProfile("Nested");
            var nested = Definitions();
            nested[0].QualityProfileId = _parent.Id;
            Action nest = () => Subject.Save(nestedParent.Id, nested);
            nest.Should().Throw<InvalidOperationException>();

            var used = CreateProfile("Used");
            Db.Insert(Builder<Series>.CreateNew().With(s => s.QualityProfileId = used.Id).BuildNew());
            Action saveUsed = () => Subject.Save(used.Id, Definitions());
            saveUsed.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void rejects_invalid_definitions_and_deletion_with_version_history()
        {
            var invalid = Definitions();
            invalid[1].Label = "SDR";
            Action save = () => Subject.Save(_parent.Id, invalid);
            save.Should().Throw<ValidationException>();

            var versions = Subject.Save(_parent.Id, Definitions());
            Db.Insert(new SeriesVersion { SeriesId = 42, CompositeProfileVersionId = versions[0].Id, Retired = true });
            Action delete = () => Subject.Delete(_parent.Id);
            delete.Should().Throw<InvalidOperationException>();
            Subject.Get(_parent.Id).Should().HaveCount(2);
        }
    }
}
