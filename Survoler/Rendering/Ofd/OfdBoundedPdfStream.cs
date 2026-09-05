using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using Survoler.Resources;
using static Survoler.Rendering.OfdXml;

namespace Survoler.Rendering;

internal sealed class OfdBoundedPdfStream : SKAbstractManagedWStream
{
    private const long MaxBytes = 64L * 1024 * 1024;
    private readonly Stream _destination;
    private readonly CancellationToken _token;
    private readonly byte[] _buffer = new byte[65536];
    private long _written;
    private bool _truncated;
    private Exception? _failure;

    internal OfdBoundedPdfStream(Stream destination, CancellationToken token)
    {
        _destination = destination;
        _token = token;
    }

    protected override bool OnWrite(IntPtr buffer, IntPtr size)
    {
        // Never unwind a managed exception through a native Skia callback.
        try
        {
            if (_truncated || _failure is not null || _token.IsCancellationRequested) return false;
            long count = size.ToInt64();
            if (count < 0 || count > MaxBytes - _written)
            {
                _truncated = true;
                return false;
            }
            int offset = 0;
            while (offset < count)
            {
                if (_token.IsCancellationRequested) return false;
                int length = (int)Math.Min(_buffer.Length, count - offset);
                Marshal.Copy(IntPtr.Add(buffer, offset), _buffer, 0, length);
                _destination.Write(_buffer, 0, length);
                _written += length;
                offset += length;
            }
            return true;
        }
        catch (Exception exception)
        {
            _failure = exception;
            return false;
        }
    }

    protected override void OnFlush()
    {
        try
        {
            if (!_truncated && _failure is null && !_token.IsCancellationRequested) _destination.Flush();
        }
        catch (Exception exception) { _failure = exception; }
    }

    protected override IntPtr OnBytesWritten() => new(_written);

    internal void ThrowIfFailed()
    {
        _token.ThrowIfCancellationRequested();
        if (_truncated) throw Invalid("OutputTooLarge");
        if (_failure is not null) throw new IOException(OfdStrings.Get("PdfWriteFailed"), _failure);
    }
}
