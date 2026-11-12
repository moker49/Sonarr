using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpisodeVersionImportTransferService
    {
        void Transfer(int seriesId, int importId, bool copyOnly);
    }

    // Internal executor. The normal import workflow remains disabled until
    // registration, notifications and completion are connected.
    public class EpisodeVersionImportTransferService : IEpisodeVersionImportTransferService
    {
        private readonly IEpisodeVersionImportIntentService _intents;
        private readonly IDiskProvider _disk;

        public EpisodeVersionImportTransferService(IEpisodeVersionImportIntentService intents, IDiskProvider disk)
        {
            _intents = intents;
            _disk = disk;
        }

        public void Transfer(int seriesId, int importId, bool copyOnly)
        {
            lock (EpisodeVersionImportSynchronization.For(importId))
            {
                var intent = _intents.GetActive(seriesId).SingleOrDefault(i => i.Id == importId);
                if (intent == null || intent.State != EpisodeVersionImportIntentState.Prepared || !_disk.FolderExists(Path.GetDirectoryName(intent.DestinationPath)))
                {
                    throw new InvalidOperationException("A prepared import and an existing destination folder are required.");
                }

                _intents.BeginMove(importId, copyOnly);
                try
                {
                    if (copyOnly)
                    {
                        _disk.CopyFile(intent.SourcePath, intent.DestinationPath, false);
                    }
                    else
                    {
                        _disk.MoveFile(intent.SourcePath, intent.DestinationPath, false);
                    }

                    _intents.ConfirmMoved(importId);
                }
                catch (Exception ex)
                {
                    try
                    {
                        _intents.MarkUncertain(importId, "Transfer or verification failed; inspect the source and destination before recovery.");
                    }
                    catch (Exception journalError)
                    {
                        throw new AggregateException("The transfer failed and its uncertain state could not be recorded.", ex, journalError);
                    }

                    throw;
                }
            }
        }
    }
}
