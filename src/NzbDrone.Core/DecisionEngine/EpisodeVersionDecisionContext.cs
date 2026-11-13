using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IEpisodeVersionDecisionContext
    {
        RemoteEpisode ForVersion(RemoteEpisode source, int seriesVersionId);
        List<RemoteEpisode> ForAllVersions(RemoteEpisode source);
    }

    // Constructs an isolated view for a release, including every episode in a
    // pack. It does not dispatch downloads or mutate the persisted series.
    public class EpisodeVersionDecisionContext : IEpisodeVersionDecisionContext
    {
        private readonly IEpisodeVersionResolver _versions;
        private readonly IQualityProfileService _profiles;
        private readonly IMediaFileService _files;

        public EpisodeVersionDecisionContext(IEpisodeVersionResolver versions, IQualityProfileService profiles, IMediaFileService files)
        {
            _versions = versions;
            _profiles = profiles;
            _files = files;
        }

        public RemoteEpisode ForVersion(RemoteEpisode source, int seriesVersionId)
        {
            ValidateSource(source);
            if (seriesVersionId <= 0)
            {
                throw new ArgumentException("A positive series version ID is required.", nameof(seriesVersionId));
            }

            return CreateContext(source, seriesVersionId, _versions.ResolveMany(source.Episodes.Select(e => e.Id).ToArray()));
        }

        public List<RemoteEpisode> ForAllVersions(RemoteEpisode source)
        {
            ValidateSource(source);
            var resolved = _versions.ResolveMany(source.Episodes.Select(e => e.Id).ToArray());
            if (resolved == null || !resolved.TryGetValue(source.Episodes[0].Id, out var options) || options == null || options.Count != 2 ||
                options.Any(v => !v.SeriesVersionId.HasValue || v.SeriesVersionId <= 0 || !v.CompositeProfileVersionId.HasValue || v.CompositeProfileVersionId <= 0) ||
                options.Select(v => v.SeriesVersionId).Distinct().Count() != 2 ||
                source.Episodes.Any(e => !resolved.TryGetValue(e.Id, out var rows) || rows == null || rows.Count != 2 ||
                    !rows.Select(v => v.SeriesVersionId).ToHashSet().SetEquals(options.Select(v => v.SeriesVersionId))))
            {
                throw new InvalidOperationException("The release does not have two complete composite version contexts.");
            }

            new CompositeProfileValidator().ValidateAndThrow(options.Select(v => new CompositeProfileVersion
            {
                Id = v.CompositeProfileVersionId.Value,
                QualityProfileId = v.QualityProfileId,
                Label = v.Label,
                Priority = v.Priority,
                IsPrimary = v.IsPrimary
            }).ToList());
            return options.OrderBy(v => v.Priority).Select(v => CreateContext(source, v.SeriesVersionId.Value, resolved)).ToList();
        }

        private static void ValidateSource(RemoteEpisode source)
        {
            if (source?.Series == null || source.Series.Id <= 0 || source.Episodes == null || source.Episodes.Count == 0 ||
                source.Episodes.Any(e => e == null || e.Id <= 0 || e.SeriesId != source.Series.Id) || source.Episodes.Select(e => e.Id).Distinct().Count() != source.Episodes.Count)
            {
                throw new ArgumentException("The release must identify one series and a nonempty set of distinct episodes.");
            }
        }

        private RemoteEpisode CreateContext(RemoteEpisode source, int seriesVersionId, Dictionary<int, List<ResolvedEpisodeVersion>> resolved)
        {
            if (resolved == null)
            {
                throw new InvalidOperationException("The episode versions could not be resolved.");
            }

            var selections = new Dictionary<int, ResolvedEpisodeVersion>();
            foreach (var episode in source.Episodes)
            {
                if (!resolved.TryGetValue(episode.Id, out var versions) || versions == null)
                {
                    throw new InvalidOperationException("The episode version could not be resolved.");
                }

                var matching = versions.Where(v => v.SeriesVersionId == seriesVersionId).ToList();
                if (matching.Count != 1 || matching[0].SeriesId != source.Series.Id || matching[0].ParentQualityProfileId != source.Series.QualityProfileId ||
                    matching[0].Monitored != (source.Series.Monitored && episode.Monitored))
                {
                    throw new InvalidOperationException("The series or episode changed while preparing the version context.");
                }

                selections.Add(episode.Id, matching[0]);
            }

            var profileIds = selections.Values.Select(v => v.QualityProfileId).Distinct().ToArray();
            var labels = selections.Values.Select(v => v.Label).Distinct(StringComparer.Ordinal).ToArray();
            if (profileIds.Length != 1 || labels.Length != 1 || !CompositeProfileValidator.ValidLabel(labels[0]) ||
                selections.Values.Select(v => (v.CompositeProfileVersionId, v.Priority, v.IsPrimary)).Distinct().Count() != 1)
            {
                throw new InvalidOperationException("The version resolves to conflicting quality profiles or invalid labels.");
            }

            var profile = _profiles.Get(profileIds[0]);
            if (profile == null || profile.Id != profileIds[0])
            {
                throw new InvalidOperationException("The version's quality profile no longer exists.");
            }

            var fileIds = selections.Values.Where(v => v.EpisodeFileId.HasValue).Select(v => v.EpisodeFileId.Value).Distinct().ToArray();
            var files = fileIds.Length == 0 ? new List<EpisodeFile>() : _files.GetFiles(fileIds);
            if (files == null || files.Any(f => f == null || f.SeriesId != source.Series.Id) || !fileIds.ToHashSet().SetEquals(files.Select(f => f.Id)))
            {
                throw new InvalidOperationException("A version file no longer belongs to the series.");
            }

            var fileLookup = files.ToDictionary(f => f.Id);
            var series = source.Series.CloneForVersion();
            series.QualityProfileId = profile.Id;
            series.QualityProfile = profile;
            var context = source.CloneForVersion();
            context.Series = series;
            context.SeriesVersionId = seriesVersionId;
            context.SeriesVersionLabel = labels[0];
            context.SeriesVersionPriority = selections.Values.First().Priority;
            context.Episodes = source.Episodes.Select(episode =>
            {
                var copy = episode.CloneForVersion();
                var selection = selections[episode.Id];
                copy.Series = series;
                copy.EpisodeFileId = selection.EpisodeFileId ?? 0;
                copy.EpisodeFile = new LazyLoaded<EpisodeFile>(selection.EpisodeFileId.HasValue ? fileLookup[selection.EpisodeFileId.Value] : null);
                return copy;
            }).ToList();
            return context;
        }
    }
}
