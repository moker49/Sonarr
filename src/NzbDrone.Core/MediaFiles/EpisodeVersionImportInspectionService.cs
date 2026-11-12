using System;
using System.Linq;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    public enum EpisodeVersionImportObservation
    {
        Prepared,
        SourceVerifiedDestinationAbsent,
        DestinationVerified,
        NeedsReview
    }

    public enum EpisodeVersionImportFileObservation
    {
        Absent,
        Present,
        Matches,
        Different,
        Unavailable
    }

    public class EpisodeVersionImportInspection
    {
        public EpisodeVersionImportIntent Intent { get; set; }
        public EpisodeVersionImportObservation Observation { get; set; }
        public EpisodeVersionImportFileObservation Source { get; set; }
        public EpisodeVersionImportFileObservation Destination { get; set; }
    }

    public interface IEpisodeVersionImportInspectionService
    {
        EpisodeVersionImportInspection Inspect(int seriesId, int importId);
    }

    // Inspection is read-only and describes a point-in-time observation. It
    // never advances state, releases locks, or assumes files remain unchanged.
    public class EpisodeVersionImportInspectionService : IEpisodeVersionImportInspectionService
    {
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IEpisodeImportFileFingerprintService _fingerprints;
        private readonly IDiskProvider _disk;

        public EpisodeVersionImportInspectionService(IEpisodeVersionImportIntentService intents, IEpisodeImportFileFingerprintService fingerprints, IDiskProvider disk)
        {
            _intents = intents;
            _fingerprints = fingerprints;
            _disk = disk;
        }

        public EpisodeVersionImportInspection Inspect(int seriesId, int importId)
        {
            var intent = _intents.GetActive(seriesId).SingleOrDefault(i => i.Id == importId);
            if (intent == null)
            {
                throw new InvalidOperationException("The active import does not belong to this series.");
            }

            var result = new EpisodeVersionImportInspection { Intent = intent, Observation = EpisodeVersionImportObservation.NeedsReview };
            if (intent.State == EpisodeVersionImportIntentState.Prepared)
            {
                // Prepared records have no source fingerprint yet.
                result.Source = ObservePresence(intent.SourcePath);
                result.Destination = ObservePresence(intent.DestinationPath);
                if (result.Source == EpisodeVersionImportFileObservation.Present && result.Destination == EpisodeVersionImportFileObservation.Absent)
                {
                    result.Observation = EpisodeVersionImportObservation.Prepared;
                }

                return result;
            }

            if (!intent.SourceSize.HasValue || intent.SourceSize <= 0 || intent.SourceHash?.Length != 64 || !intent.CopyOnly.HasValue)
            {
                result.Source = EpisodeVersionImportFileObservation.Unavailable;
                result.Destination = EpisodeVersionImportFileObservation.Unavailable;
                return result;
            }

            result.Source = Observe(intent.SourcePath, intent);
            result.Destination = Observe(intent.DestinationPath, intent);
            if ((intent.State == EpisodeVersionImportIntentState.Moving || intent.State == EpisodeVersionImportIntentState.Uncertain) && result.Source == EpisodeVersionImportFileObservation.Matches && result.Destination == EpisodeVersionImportFileObservation.Absent)
            {
                result.Observation = EpisodeVersionImportObservation.SourceVerifiedDestinationAbsent;
            }
            else if (result.Destination == EpisodeVersionImportFileObservation.Matches &&
                (intent.CopyOnly.Value ? result.Source == EpisodeVersionImportFileObservation.Matches : result.Source == EpisodeVersionImportFileObservation.Absent))
            {
                result.Observation = EpisodeVersionImportObservation.DestinationVerified;
            }

            return result;
        }

        private EpisodeVersionImportFileObservation ObservePresence(string path)
        {
            try
            {
                return _disk.FileExists(path) ? EpisodeVersionImportFileObservation.Present : EpisodeVersionImportFileObservation.Absent;
            }
            catch (Exception)
            {
                return EpisodeVersionImportFileObservation.Unavailable;
            }
        }

        private EpisodeVersionImportFileObservation Observe(string path, EpisodeVersionImportIntent intent)
        {
            try
            {
                if (!_disk.FileExists(path))
                {
                    return EpisodeVersionImportFileObservation.Absent;
                }

                var actual = _fingerprints.Read(path);
                return actual != null && actual.Size == intent.SourceSize && string.Equals(actual.Hash, intent.SourceHash, StringComparison.Ordinal)
                    ? EpisodeVersionImportFileObservation.Matches
                    : EpisodeVersionImportFileObservation.Different;
            }
            catch (Exception)
            {
                return EpisodeVersionImportFileObservation.Unavailable;
            }
        }
    }
}
