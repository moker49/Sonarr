using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class CompositeProfileValidatorFixture
    {
        private static List<CompositeProfileVersion> ValidDefinitions()
        {
            return new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { QualityProfileId = 2, Label = "HDR", Priority = 1 }
            };
        }

        [Test]
        public void accepts_two_distinct_versions_with_one_primary()
        {
            Assert.That(new CompositeProfileValidator().Validate(ValidDefinitions()).IsValid, Is.True);
        }

        [TestCase("same_label")]
        [TestCase("unsafe_label")]
        [TestCase("same_profile")]
        [TestCase("same_priority")]
        [TestCase("two_primary")]
        [TestCase("one_version")]
        public void rejects_ambiguous_definitions(string problem)
        {
            var definitions = ValidDefinitions();
            switch (problem)
            {
                case "same_label": definitions[1].Label = "sdr"; break;
                case "unsafe_label": definitions[1].Label = "HDR/unsafe"; break;
                case "same_profile": definitions[1].QualityProfileId = 1; break;
                case "same_priority": definitions[1].Priority = 0; break;
                case "two_primary": definitions[1].IsPrimary = true; break;
                case "one_version": definitions.RemoveAt(1); break;
            }

            Assert.That(new CompositeProfileValidator().Validate(definitions).IsValid, Is.False);
        }
    }
}
