using System;
using System.Linq;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionImportWorkflow
    {
        EpisodeFile Resume(int seriesId, int importId, bool copyOnly, EpisodeFile metadata);
    }

    // Internal workflow for an existing intent. Notifications and completion
    // still need integration before normal imports can call this service.
    public class EpisodeVersionImportWorkflow : IEpisodeVersionImportWorkflow
    {
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IEpisodeVersionImportTransferService _transfers;
        private readonly IMediaFileService _files;
        private readonly IEpisodeVersionResolver _versions;
        private readonly IEpisodeVersionImportCompletionService _completion;

        public EpisodeVersionImportWorkflow(IEpisodeVersionImportIntentService intents, IEpisodeVersionImportTransferService transfers, IMediaFileService files, IEpisodeVersionResolver versions, IEpisodeVersionImportCompletionService completion)
        {
            _intents = intents;
            _transfers = transfers;
            _files = files;
            _versions = versions;
            _completion = completion;
        }

        public EpisodeFile Resume(int seriesId, int importId, bool copyOnly, EpisodeFile metadata)
        {
            lock (EpisodeVersionImportSynchronization.For(importId))
            {
                var intent = Find(seriesId, importId);
                if (intent.State == EpisodeVersionImportIntentState.Moving || intent.State == EpisodeVersionImportIntentState.Uncertain)
                {
                    throw new InvalidOperationException("The interrupted transfer must be reconciled before resuming import.");
                }

                if (intent.CopyOnly.HasValue && intent.CopyOnly.Value != copyOnly)
                {
                    throw new InvalidOperationException("The requested transfer mode differs from the recorded import.");
                }

                if (intent.State != EpisodeVersionImportIntentState.Registered && intent.State != EpisodeVersionImportIntentState.Completed)
                {
                    if (metadata == null || metadata.Id != 0 || metadata.SeriesId != seriesId || metadata.Size <= 0 || metadata.Quality == null || metadata.Languages == null || string.IsNullOrWhiteSpace(metadata.RelativePath))
                    {
                        throw new ArgumentException("New episode file metadata is required before transferring or registering a file.");
                    }

                    if (intent.State == EpisodeVersionImportIntentState.Prepared)
                    {
                        _transfers.Transfer(seriesId, importId, copyOnly);
                        intent = Find(seriesId, importId);
                    }

                    if (intent.State != EpisodeVersionImportIntentState.Moved)
                    {
                        throw new InvalidOperationException("The import is not ready for registration.");
                    }

                    _intents.RegisterMovedFile(seriesId, importId, metadata);
                    intent = Find(seriesId, importId);
                }

                if ((intent.State != EpisodeVersionImportIntentState.Registered && intent.State != EpisodeVersionImportIntentState.Completed) || !intent.RegisteredFileId.HasValue)
                {
                    throw new InvalidOperationException("The import has no registered file.");
                }

                var ids = _intents.GetEpisodeIds(importId).ToArray();
                if (ids.Length == 0)
                {
                    throw new InvalidOperationException("The registered import has no episode coverage.");
                }

                var resolved = _versions.ResolveMany(ids);
                if (ids.Any(id => !resolved.TryGetValue(id, out var versions) || versions.Count(v => v.SeriesId == seriesId && v.SeriesVersionId == intent.SeriesVersionId && v.EpisodeFileId == intent.RegisteredFileId) != 1))
                {
                    throw new InvalidOperationException("The registered file no longer owns the import's episode slots.");
                }

                var file = _files.Get(intent.RegisteredFileId.Value);
                if (file.SeriesId != seriesId || file.Size != intent.SourceSize)
                {
                    throw new InvalidOperationException("The registered file metadata no longer matches the import.");
                }

                if (intent.State == EpisodeVersionImportIntentState.Registered)
                {
                    _completion.Complete(seriesId, importId);
                }

                return file;
            }
        }

        private EpisodeVersionImportIntent Find(int seriesId, int importId)
        {
            return _intents.GetForSeries(seriesId).SingleOrDefault(i => i.Id == importId)
                ?? throw new InvalidOperationException("The import does not belong to this series.");
        }
    }
}
