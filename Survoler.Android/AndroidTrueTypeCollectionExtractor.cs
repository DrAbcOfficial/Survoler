using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OfficeIMO.Pdf;

namespace Survoler.Android;

internal static class AndroidTrueTypeCollectionExtractor
{
    private const int MaxFaces = 64;
    private const int MaxExtractedFaceBytes = 64 * 1024 * 1024;
    private const string CoverageProbe =
        "Survoler ABC 123 \u4e2d\u6587\u5b57\u4f53\u66ff\u6362\u6d4b\u8bd5\uff0c\u3002\u5fae\u8f6f\u96c5\u9ed1\u5b8b\u4f53\u9ed1\u4f53\u7b49\u7ebf\u4eff\u5b8b\u6977\u4f53";

    public static bool TryExtractPreferredFace(string path, out byte[]? fontData)
    {
        fontData = null;
        try
        {
            byte[] collection = File.ReadAllBytes(path);
            if (!IsCollection(collection))
            {
                return false;
            }

            EnsureRange(collection, 0, 12);
            uint faceCount = ReadUInt32(collection, 8);
            if (faceCount == 0 || faceCount > MaxFaces)
            {
                return false;
            }

            EnsureRange(collection, 12, checked((int)faceCount * 4));
            int bestScore = int.MinValue;
            for (int index = 0; index < faceCount; index++)
            {
                try
                {
                    uint offset = ReadUInt32(collection, 12 + index * 4);
                    if (offset > int.MaxValue)
                    {
                        continue;
                    }

                    byte[] candidate = ExtractFace(collection, (int)offset);
                    int missingGlyphs = PdfTextPreflight.AnalyzeEmbeddedFont(
                        CoverageProbe,
                        candidate,
                        fontName: "Android system font").Count;
                    int score = -missingGlyphs * 100 + ScoreNames(ReadNames(candidate));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        fontData = candidate;
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or ArithmeticException or FormatException or
                    IndexOutOfRangeException or NotSupportedException)
                {
                }
            }

            return fontData is not null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            ArithmeticException or FormatException or IndexOutOfRangeException or NotSupportedException)
        {
            fontData = null;
            return false;
        }
    }

    private static byte[] ExtractFace(byte[] collection, int faceOffset)
    {
        EnsureRange(collection, faceOffset, 12);
        ushort tableCount = ReadUInt16(collection, faceOffset + 4);
        if (tableCount == 0)
        {
            throw new NotSupportedException("The font collection face has no tables.");
        }

        int directoryLength = checked(12 + tableCount * 16);
        EnsureRange(collection, faceOffset, directoryLength);
        var tables = new List<TableRecord>(tableCount);
        int outputOffset = Align4(directoryLength);

        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = faceOffset + 12 + index * 16;
            uint sourceOffset = ReadUInt32(collection, recordOffset + 8);
            uint length = ReadUInt32(collection, recordOffset + 12);
            if (sourceOffset > int.MaxValue || length > int.MaxValue)
            {
                throw new NotSupportedException("The font table is too large.");
            }

            EnsureRange(collection, (int)sourceOffset, (int)length);
            tables.Add(new TableRecord(
                Encoding.ASCII.GetString(collection, recordOffset, 4),
                ReadUInt32(collection, recordOffset + 4),
                (int)sourceOffset,
                (int)length,
                outputOffset));
            outputOffset = Align4(checked(outputOffset + (int)length));
        }

        if (outputOffset > MaxExtractedFaceBytes)
        {
            throw new NotSupportedException("The extracted font face is too large.");
        }

        byte[] font = new byte[outputOffset];
        Array.Copy(collection, faceOffset, font, 0, 4);
        WriteUInt16(font, 4, tableCount);
        WriteSearchParameters(font, tableCount);

        for (int index = 0; index < tables.Count; index++)
        {
            TableRecord table = tables[index];
            int targetRecordOffset = 12 + index * 16;
            byte[] tag = Encoding.ASCII.GetBytes(table.Tag);
            Array.Copy(tag, 0, font, targetRecordOffset, 4);
            WriteUInt32(font, targetRecordOffset + 4, table.Checksum);
            WriteUInt32(font, targetRecordOffset + 8, (uint)table.TargetOffset);
            WriteUInt32(font, targetRecordOffset + 12, (uint)table.Length);
            Array.Copy(collection, table.SourceOffset, font, table.TargetOffset, table.Length);
        }

        return font;
    }

    private static IReadOnlyList<string> ReadNames(byte[] font)
    {
        EnsureRange(font, 0, 12);
        ushort tableCount = ReadUInt16(font, 4);
        EnsureRange(font, 12, checked(tableCount * 16));
        int nameTableOffset = -1;
        int nameTableLength = 0;

        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = 12 + index * 16;
            if (Encoding.ASCII.GetString(font, recordOffset, 4) != "name")
            {
                continue;
            }

            nameTableOffset = checked((int)ReadUInt32(font, recordOffset + 8));
            nameTableLength = checked((int)ReadUInt32(font, recordOffset + 12));
            break;
        }

        if (nameTableOffset < 0)
        {
            return Array.Empty<string>();
        }

        EnsureRange(font, nameTableOffset, nameTableLength);
        ushort recordCount = ReadUInt16(font, nameTableOffset + 2);
        ushort stringsOffset = ReadUInt16(font, nameTableOffset + 4);
        EnsureRange(font, nameTableOffset + 6, checked(recordCount * 12));
        var names = new List<string>();

        for (int index = 0; index < recordCount; index++)
        {
            int recordOffset = nameTableOffset + 6 + index * 12;
            ushort platformId = ReadUInt16(font, recordOffset);
            ushort nameId = ReadUInt16(font, recordOffset + 6);
            if (nameId is not (1 or 4 or 16))
            {
                continue;
            }

            ushort length = ReadUInt16(font, recordOffset + 8);
            ushort offset = ReadUInt16(font, recordOffset + 10);
            int valueOffset = checked(nameTableOffset + stringsOffset + offset);
            EnsureRange(font, valueOffset, length);
            string value = platformId is 0 or 3
                ? Encoding.BigEndianUnicode.GetString(font, valueOffset, length)
                : Encoding.ASCII.GetString(font, valueOffset, length);
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add(value);
            }
        }

        return names;
    }

    private static int ScoreNames(IReadOnlyList<string> names)
    {
        int score = 0;
        foreach (string name in names)
        {
            string normalized = name.ToLowerInvariant();
            if (normalized.Contains("cjk sc") || normalized.Contains("sans sc") ||
                normalized.Contains("serif sc") || normalized.Contains("simplified chinese"))
            {
                score = Math.Max(score, 200);
            }
            else if (normalized.Contains("hans"))
            {
                score = Math.Max(score, 160);
            }
            else if (normalized.Contains("cjk"))
            {
                score = Math.Max(score, 80);
            }

            if (normalized.Contains("cjk jp") || normalized.Contains("cjk kr") ||
                normalized.Contains("cjk tc") || normalized.Contains("cjk hk"))
            {
                score -= 40;
            }
        }

        return score;
    }

    private static bool IsCollection(byte[] data) =>
        data.Length >= 4 && data[0] == (byte)'t' && data[1] == (byte)'t' &&
        data[2] == (byte)'c' && data[3] == (byte)'f';

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        EnsureRange(data, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        EnsureRange(data, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        EnsureRange(data, offset, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        EnsureRange(data, offset, 4);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteSearchParameters(byte[] data, int tableCount)
    {
        int maxPowerOfTwo = 1;
        int entrySelector = 0;
        while (maxPowerOfTwo * 2 <= tableCount)
        {
            maxPowerOfTwo *= 2;
            entrySelector++;
        }

        WriteUInt16(data, 6, (ushort)(maxPowerOfTwo * 16));
        WriteUInt16(data, 8, (ushort)entrySelector);
        WriteUInt16(data, 10, (ushort)((tableCount - maxPowerOfTwo) * 16));
    }

    private static void EnsureRange(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new FormatException("The font contains an invalid offset or length.");
        }
    }

    private readonly record struct TableRecord(
        string Tag,
        uint Checksum,
        int SourceOffset,
        int Length,
        int TargetOffset);
}
