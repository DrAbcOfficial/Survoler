using System;

namespace Survoler.Documents;

public sealed class DocumentOpenException : Exception
{
    public DocumentOpenException(string message) : base(message)
    {
    }
}
