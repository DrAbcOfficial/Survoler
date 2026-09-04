using System;
using Avalonia.Platform.Storage;

namespace Survoler.Documents;

public sealed class DocumentActivationService
{
    private IStorageFile? _pendingFile;

    public event EventHandler<IStorageFile>? FileActivated;

    public void Publish(IStorageFile file)
    {
        EventHandler<IStorageFile>? handler = FileActivated;
        if (handler is null)
        {
            _pendingFile = file;
            return;
        }

        _pendingFile = null;
        handler(this, file);
    }

    public bool TryTakePending(out IStorageFile? file)
    {
        file = _pendingFile;
        _pendingFile = null;
        return file is not null;
    }
}
