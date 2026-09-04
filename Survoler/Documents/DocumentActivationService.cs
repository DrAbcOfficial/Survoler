using System;
using Avalonia.Platform.Storage;

namespace Survoler.Documents;

public sealed class DocumentActivationService
{
    private IStorageFile? _pendingFile;

    public event EventHandler<IStorageFile>? FileActivated;

    public void Publish(IStorageFile file)
    {
        _pendingFile = file;
        FileActivated?.Invoke(this, file);
    }

    public bool TryTakePending(out IStorageFile? file)
    {
        file = _pendingFile;
        _pendingFile = null;
        return file is not null;
    }
}
