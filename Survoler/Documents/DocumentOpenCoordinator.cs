using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace Survoler.Documents;

public sealed class DocumentOpenCoordinator : IDisposable
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _requestCancellation;
    private DocumentSession? _currentSession;

    public async Task<DocumentSession?> OpenAsync(
        IStorageFile file,
        IProgress<DocumentLoadProgress>? progress = null)
    {
        if (!OfficeFileKinds.TryFromFileName(file.Name, out OfficeFileKind kind))
        {
            throw new DocumentOpenException("This file type is not supported.");
        }

        var requestCancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation;
        lock (_sync)
        {
            previousCancellation = _requestCancellation;
            _requestCancellation = requestCancellation;
        }

        previousCancellation?.Cancel();

        CancellationToken cancellationToken = requestCancellation.Token;
        bool enteredGate = false;
        string? localPath = null;

        try
        {
            await _gate.WaitAsync(cancellationToken);
            enteredGate = true;

            await using Stream source = await file.OpenReadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            long? totalBytes = GetLength(source);
            if (totalBytes > PreviewLimits.MaxInputBytes)
            {
                throw new DocumentOpenException("This file is too large for quick preview.");
            }

            string cacheDirectory = Path.Combine(Path.GetTempPath(), "survoler");
            Directory.CreateDirectory(cacheDirectory);
            localPath = Path.Combine(
                cacheDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(file.Name).ToLowerInvariant()}");

            await using (var destination = new FileStream(
                localPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithLimitAsync(source, destination, totalBytes, progress, cancellationToken);
            }

            await ValidateSignatureAsync(localPath, kind, cancellationToken);

            var session = new DocumentSession(Guid.NewGuid(), file.Name, localPath, kind);
            localPath = null;

            DocumentSession? previousSession;
            lock (_sync)
            {
                if (!ReferenceEquals(_requestCancellation, requestCancellation))
                {
                    session.Dispose();
                    return null;
                }

                previousSession = _currentSession;
                _currentSession = session;
            }

            previousSession?.Dispose();
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (localPath is not null)
            {
                TryDelete(localPath);
            }

            if (enteredGate)
            {
                _gate.Release();
            }

            lock (_sync)
            {
                if (!ReferenceEquals(_requestCancellation, requestCancellation))
                {
                    requestCancellation.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            _currentSession?.Dispose();
            _currentSession = null;
        }

        _gate.Dispose();
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        IProgress<DocumentLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long bytesRead = 0;

        try
        {
            while (true)
            {
                int count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
                if (bytesRead > PreviewLimits.MaxInputBytes)
                {
                    throw new DocumentOpenException("This file is too large for quick preview.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                progress?.Report(new DocumentLoadProgress(bytesRead, totalBytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ValidateSignatureAsync(
        string path,
        OfficeFileKind kind,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        int count = await stream.ReadAsync(header.AsMemory(), cancellationToken);
        bool isCompound = count >= 8 &&
            header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0 &&
            header[4] == 0xA1 && header[5] == 0xB1 && header[6] == 0x1A && header[7] == 0xE1;
        bool isZip = count >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
            ((header[2] == 0x03 && header[3] == 0x04) ||
             (header[2] == 0x05 && header[3] == 0x06) ||
             (header[2] == 0x07 && header[3] == 0x08));

        if ((kind.IsLegacy() && !isCompound) || (!kind.IsLegacy() && !isZip))
        {
            throw new DocumentOpenException("The file content does not match its extension.");
        }
    }

    private static long? GetLength(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return null;
        }

        try
        {
            return stream.Length;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
