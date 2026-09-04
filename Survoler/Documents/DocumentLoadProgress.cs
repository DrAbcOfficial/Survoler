namespace Survoler.Documents;

public readonly record struct DocumentLoadProgress(long BytesRead, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0
        ? (double)BytesRead / TotalBytes.Value
        : null;
}
