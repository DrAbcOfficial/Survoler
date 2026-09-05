using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Survoler.Rendering;

public sealed class ConvertedPdfDocument : IDisposable
{
    private int _disposed;

    public ConvertedPdfDocument(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static async Task<ConvertedPdfDocument> CopyFromAsync(
        string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "survoler");
        Directory.CreateDirectory(directory);
        var document = new ConvertedPdfDocument(
            System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}.pdf"));
        try
        {
            // Preview ownership is independent of the input session, which may be replaced first.
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(document.Path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await source.CopyToAsync(destination, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

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
