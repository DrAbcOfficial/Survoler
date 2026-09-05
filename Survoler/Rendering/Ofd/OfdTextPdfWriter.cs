using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OfficeIMO.Drawing;
using SkiaSharp;
using Survoler.Documents;
using Survoler.Resources;

namespace Survoler.Rendering;

internal static class OfdTextPdfWriter
{
    internal static void Write(List<List<string>> textPages, string output,
        OfficePdfRenderingResources? resources, CancellationToken token)
    {
        OfficeFontFace[] registered = resources?.Profile.Fonts.Faces
            .OrderBy(f => f.Style == OfficeFontStyle.Regular ? 0 : 1).ToArray() ?? Array.Empty<OfficeFontFace>();
        var faces = new List<SKTypeface>();
        var fonts = new List<SKFont>();
        var metrics = new Dictionary<int, (SKFont Font, float Width)>();
        int loadedRegistered = 0;
        long fontBytes = 0;
        try
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var stream = new OfdBoundedPdfStream(file, token);
            using var pdf = SKDocument.CreatePdf(stream);
            stream.ThrowIfFailed();
            if (pdf is null) throw Invalid("PdfWriterUnavailable");
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            const float width = 595.2756f, height = 841.8898f, margin = 36, lineHeight = 18;
            SKCanvas? canvas = null;
            int outputPages = 0;
            float x = margin, y = margin + 12, runX = margin;
            var run = new StringBuilder();
            SKFont? runFont = null;
            foreach (List<string> paragraphs in textPages)
            {
                BeginPage();
                foreach (string paragraph in paragraphs.Prepend(OfdStrings.Get("TextOnlyPreviewTitle")))
                {
                    bool previousCr = false;
                    int column = 0;
                    foreach (Rune rune in paragraph.EnumerateRunes())
                    {
                        token.ThrowIfCancellationRequested();
                        if (rune.Value == '\n' && previousCr) { previousCr = false; continue; }
                        previousCr = rune.Value == '\r';
                        if (rune.Value is '\r' or '\n')
                        {
                            NewLine();
                            column = 0;
                            continue;
                        }
                        if (rune.Value == '\t')
                        {
                            int spaces = 4 - column % 4;
                            for (int i = 0; i < spaces; i++) DrawRune(new Rune(' '));
                        }
                        else DrawRune(rune);
                    }
                    NewLine();

                    void DrawRune(Rune rune)
                    {
                        (SKFont font, float advance) = Measure(rune);
                        if (advance > width - 2 * margin) throw Invalid("CoordinateRange");
                        if (x + advance > width - margin) { NewLine(); column = 0; }
                        if (y > height - margin) { EndPage(); BeginPage(); }
                        if (runFont != font) FlushRun();
                        if (run.Length == 0) { runFont = font; runX = x; }
                        run.Append(rune.ToString());
                        x += advance;
                        column++;
                    }
                }
                EndPage();
            }
            token.ThrowIfCancellationRequested();
            pdf.Close();
            stream.Flush();
            stream.ThrowIfFailed();
            return;

            void FlushRun()
            {
                if (run.Length == 0) return;
                OfdPdfText.Draw(canvas!, run.ToString(), runX, y, runFont!, paint);
                run.Clear();
            }

            void NewLine()
            {
                if (y > height - margin) { EndPage(); BeginPage(); }
                FlushRun();
                x = margin;
                y += lineHeight;
            }

            void BeginPage()
            {
                token.ThrowIfCancellationRequested();
                if (++outputPages > PreviewLimits.MaxPdfPages) throw Invalid("PageCount");
                canvas = pdf.BeginPage(width, height);
                stream.ThrowIfFailed();
                x = margin;
                y = margin + 12;
            }

            void EndPage()
            {
                FlushRun();
                pdf.EndPage();
                stream.Flush();
                stream.ThrowIfFailed();
            }

            (SKFont Font, float Width) Measure(Rune rune)
            {
                if (metrics.TryGetValue(rune.Value, out var result)) return result;
                string text = rune.ToString();
                SKFont? font = fonts.FirstOrDefault(f => f.ContainsGlyphs(text));
                while (font is null && loadedRegistered < registered.Length)
                {
                    byte[] bytes = registered[loadedRegistered++].Data;
                    ReserveFontBytes(bytes.Length);
                    using SKData data = SKData.CreateCopy(bytes);
                    SKFont candidate = Own(SKTypeface.FromData(data) ?? throw Invalid("FontLoadFailed"));
                    if (candidate.ContainsGlyphs(text)) font = candidate;
                }
                if (font is null && registered.Length == 0 && !OperatingSystem.IsAndroid())
                {
                    // Matched native faces are byte sources only: shared faces corrupt concurrent ToUnicode maps.
                    using SKTypeface matched = SKFontManager.Default.MatchCharacter(rune.Value)
                        ?? throw Invalid("NoFontCoverage");
                    using SKStreamAsset source = matched.OpenStream(out int collectionIndex)
                        ?? throw Invalid("FallbackFontReadFailed");
                    ReserveFontBytes(source.Length);
                    using SKData data = SKData.Create(source) ?? throw Invalid("FallbackFontLoadFailed");
                    font = Own(SKTypeface.FromData(data, collectionIndex) ?? throw Invalid("FallbackFontLoadFailed"));
                }
                if (font is null || !font.ContainsGlyphs(text)) throw Invalid("NoFontCoverage");
                float advance = font.MeasureText(text, paint);
                if (!float.IsFinite(advance) || advance < 0) throw Invalid("CoordinateRange");
                result = (font, advance);
                metrics.Add(rune.Value, result);
                return result;
            }
        }
        finally
        {
            foreach (SKFont font in fonts) font.Dispose();
            foreach (SKTypeface face in faces) face.Dispose();
        }

        SKFont Own(SKTypeface face)
        {
            faces.Add(face);
            var font = new SKFont(face, 12);
            fonts.Add(font);
            SKFontMetrics dimensions = font.Metrics;
            if (!float.IsFinite(dimensions.Ascent) || !float.IsFinite(dimensions.Descent) ||
                !float.IsFinite(dimensions.Leading) || dimensions.Ascent > 0 || dimensions.Descent < 0 ||
                dimensions.Descent - dimensions.Ascent > 14400) throw Invalid("CoordinateRange");
            return font;
        }

        void ReserveFontBytes(long count)
        {
            token.ThrowIfCancellationRequested();
            if (count <= 0 || count > 64L * 1024 * 1024 - fontBytes) throw Invalid("FontBudget");
            fontBytes += count;
        }
    }

    private static DocumentOpenException Invalid(string key, params object[] args) =>
        new(OfdStrings.Format("InvalidPrefix", OfdStrings.Format(key, args)));
}
