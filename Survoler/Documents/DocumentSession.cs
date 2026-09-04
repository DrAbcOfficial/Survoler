using System;
using System.IO;

namespace Survoler.Documents;

public sealed class DocumentSession : IDisposable
{
    public DocumentSession(Guid id, string sourceName, string localPath, OfficeFileKind kind)
    {
        Id = id;
        SourceName = sourceName;
        LocalPath = localPath;
        Kind = kind;
    }

    public Guid Id { get; }

    public string SourceName { get; }

    public string LocalPath { get; }

    public OfficeFileKind Kind { get; }

    public bool IsLegacy => Kind.IsLegacy();

    public void Dispose()
    {
        try
        {
            File.Delete(LocalPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
