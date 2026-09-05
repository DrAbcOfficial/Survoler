using System;
using System.IO;
using System.Threading;

namespace Survoler.Rendering;

public sealed class ConvertedPdfDocument : IDisposable
{
    private int _disposed;

    public ConvertedPdfDocument(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
