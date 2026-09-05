using System.Reflection;
using Avalonia.Platform.Storage;

namespace Survoler.Tests;

// Avalonia 12 blocks direct C# implementations of its storage interfaces.
public class FakeStorageFile : DispatchProxy
{
    private string _name = string.Empty;
    private byte[] _content = [];

    public static IStorageFile Create(string name, byte[] content)
    {
        IStorageFile file = Create<IStorageFile, FakeStorageFile>();
        var fake = (FakeStorageFile)file;
        fake._name = name;
        fake._content = content;
        return file;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name switch
        {
            "get_Name" => _name,
            nameof(IStorageFile.OpenReadAsync) =>
                Task.FromResult<Stream>(new MemoryStream(_content, writable: false)),
            nameof(IDisposable.Dispose) => null,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
}
