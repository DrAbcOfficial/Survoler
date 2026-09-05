using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Survoler.Documents;

namespace Survoler.Rendering;

internal sealed class OfdPackage : IDisposable
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XDocument> _xml = new(StringComparer.Ordinal);
    private readonly CancellationToken _token;
    private long _readBytes;
    private long _xmlBytes;
    private int _xmlNodes;

    internal OfdPackage(string path, CancellationToken token)
    {
        _token = token;
        token.ThrowIfCancellationRequested();
        _stream = File.OpenRead(path);
        try
        {
            if (_stream.Length > PreviewLimits.MaxInputBytes) throw Invalid("Input exceeds 64 MiB.");
            _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                if (_archive.Entries.Count > PreviewLimits.MaxPackageParts) throw Invalid("Too many ZIP entries.");
                long total = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in _archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                        name.Split('/').Any(p => p is "." or "..")) throw Invalid("Unsafe ZIP entry path.");
                    if (name.EndsWith('/')) continue;
                    if (string.IsNullOrEmpty(name) || name.Contains("//") || !names.Add(name))
                        throw Invalid("Empty or duplicate ZIP entry path.");
                    total = checked(total + entry.Length);
                    if (entry.Length > PreviewLimits.MaxPartBytes || total > PreviewLimits.MaxTotalUncompressedBytes ||
                        entry.Length / (double)Math.Max(1, entry.CompressedLength) > PreviewLimits.MaxCompressionRatio)
                        throw Invalid("ZIP expansion limits exceeded.");
                    _entries.Add(name, entry);
                }
                if (!_entries.ContainsKey("OFD.xml")) throw Invalid("The package has no OFD.xml entry.");
            }
            catch
            {
                _archive.Dispose();
                throw;
            }
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    internal byte[] ReadBytes(string path, long limit = PreviewLimits.MaxPartBytes)
    {
        _token.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(path, out ZipArchiveEntry? entry)) throw Invalid("Missing package entry: " + path);
        if (entry.Length > limit || entry.Length > PreviewLimits.MaxTotalUncompressedBytes - _readBytes)
            throw Invalid("Package read budget exceeded.");
        using Stream source = entry.Open();
        byte[] result = new byte[checked((int)entry.Length)];
        int offset = 0;
        while (offset < result.Length)
        {
            _token.ThrowIfCancellationRequested();
            int count = source.Read(result, offset, Math.Min(65536, result.Length - offset));
            if (count == 0) throw Invalid("Truncated package entry.");
            _readBytes += count;
            offset += count;
        }
        if (source.ReadByte() != -1) throw Invalid("Package entry exceeds its declared length.");
        return result;
    }

    internal XDocument ReadXml(string path)
    {
        _token.ThrowIfCancellationRequested();
        if (_xml.TryGetValue(path, out XDocument? cached)) return cached;
        byte[] bytes = ReadBytes(path, Math.Min(8 * 1024 * 1024, 16 * 1024 * 1024 - _xmlBytes));
        _xmlBytes += bytes.Length;
        if (bytes.Length > 8 * 1024 * 1024 || _xmlBytes > 16 * 1024 * 1024)
            throw Invalid("XML size budget exceeded.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        // Check nesting before building a DOM; both passes disable DTDs and external resolution.
        using (var input = new MemoryStream(bytes, writable: false))
        using (XmlReader reader = XmlReader.Create(input, settings))
        {
            while (reader.Read())
            {
                _token.ThrowIfCancellationRequested();
                if (reader.Depth > 64 || reader.AttributeCount > 64) throw Invalid("XML complexity limit exceeded.");
                _xmlNodes += 1 + reader.AttributeCount;
                if (_xmlNodes > 200000) throw Invalid("XML node budget exceeded.");
            }
        }
        using var data = new MemoryStream(bytes, writable: false);
        using XmlReader safeReader = XmlReader.Create(data, settings);
        XDocument document = XDocument.Load(safeReader, LoadOptions.PreserveWhitespace);
        _xml.Add(path, document);
        return document;
    }

    internal static string Resolve(string baseEntryPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains('\\') || reference.Contains(':') ||
            reference.Any(char.IsControl) || reference.Contains('?') || reference.Contains('#'))
            throw Invalid("Invalid package resource reference.");
        var segments = reference.StartsWith('/') ? new List<string>() :
            baseEntryPath.Split('/').SkipLast(1).ToList();
        foreach (string segment in reference.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw Invalid("Resource reference escapes the package.");
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        if (segments.Count == 0) throw Invalid("Resource reference does not name a file.");
        return string.Join('/', segments);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    private static InvalidDataException Invalid(string message) => new("Invalid OFD: " + message);
}
