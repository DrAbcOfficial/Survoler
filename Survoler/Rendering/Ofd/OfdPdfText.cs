using System.Text;
using SkiaSharp;

namespace Survoler.Rendering;

internal static class OfdPdfText
{
    internal static void Draw(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
    {
        int count = font.CountGlyphs(text);
        if (count == 0) return;

        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocateRawPositionedTextRun(font, count, Encoding.UTF8.GetByteCount(text));
        font.GetGlyphs(text, run.Glyphs);
        font.GetGlyphPositions(run.Glyphs, run.Positions);
        Encoding.UTF8.GetBytes(text, run.Text);

        // Preserve source scalars when multiple Unicode characters share a font glyph.
        // Skia's PDF backend needs UTF-8 byte offsets, not UTF-16 character indices.
        int index = 0;
        uint offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            run.Clusters[index++] = offset;
            offset += (uint)rune.Utf8SequenceLength;
        }

        using var blob = builder.Build();
        canvas.DrawText(blob!, x, y, paint);
    }
}
