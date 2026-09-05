using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Survoler.Resources;
using static Survoler.Rendering.OfdXml;

namespace Survoler.Rendering;

internal sealed partial class OfdPdfRenderer
{
    private void PaintText(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope, byte alpha)
    {
        Resource definition = Lookup(scope, Required(obj, "Font"), "Font");
        float size = Positive(Required(obj, "Size"));
        XElement[] codes = obj.Elements(Ofd + "TextCode").ToArray();
        if (codes.Length == 0) throw Invalid("MissingTextCode");
        var fontText = new StringBuilder();
        foreach (XElement code in codes)
        {
            _token.ThrowIfCancellationRequested();
            string value = code.Value;
            if (value.Length > MaxText - _text - fontText.Length)
                throw Invalid("TextBudget");
            fontText.Append(value);
        }
        using var paint = new SKPaint { IsAntialias = true, Color = Color(obj.Element(Ofd + "FillColor"), scope, alpha) };
        using var font = new SKFont(Typeface(definition, fontText.ToString()), size);
        float? previousX = null, previousY = null;
        foreach (XElement code in codes)
        {
            Tick();
            Check(code, "X Y DeltaX DeltaY", "", allowText: true);
            string text = code.Value;
            _text = checked(_text + text.Length);
            if (_text > MaxText) throw Invalid("TextBudget");
            if (text.Length == 0) throw Invalid("EmptyTextCode");
            if (!font.ContainsGlyphs(text)) throw Invalid("MissingGlyphs");
            float x = code.Attribute("X") is { } xAttribute ? Number(xAttribute.Value) :
                previousX ?? throw Invalid("FirstTextCodeX");
            float y = code.Attribute("Y") is { } yAttribute ? Number(yAttribute.Value) :
                previousY ?? throw Invalid("FirstTextCodeY");
            Rune[] runes = text.EnumerateRunes().ToArray();
            if (code.Attribute("DeltaX") is null && code.Attribute("DeltaY") is null)
            {
                canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
                int lastLength = runes[^1].Utf16SequenceLength;
                previousX = Finite(x + font.MeasureText(text[..^lastLength], paint));
                previousY = y;
                continue;
            }
            float[] dx = Deltas(code.Attribute("DeltaX")?.Value, runes.Length);
            float[] dy = Deltas(code.Attribute("DeltaY")?.Value, runes.Length);
            for (int i = 0; i < runes.Length; i++)
            {
                _token.ThrowIfCancellationRequested();
                string glyph = runes[i].ToString();
                canvas.DrawText(glyph, x, y, SKTextAlign.Left, font, paint);
                if (i + 1 == runes.Length) break;
                x = Finite(x + (dx.Length == 0 ? 0 : dx[i]));
                y = Finite(y + (dy.Length == 0 ? 0 : dy[i]));
            }
            previousX = x;
            previousY = y;
        }
    }

    private float[] Deltas(string? value, int count)
    {
        if (value is null) return Array.Empty<float>();
        string[] tokens = Words(value);
        if (tokens.Length == 0) throw Invalid("EmptyTextDelta");
        var result = new List<float>();
        for (int i = 0; i < tokens.Length; i++)
        {
            _token.ThrowIfCancellationRequested();
            int repeat = 1;
            if (tokens[i] == "g")
            {
                if (i + 2 >= tokens.Length || !int.TryParse(tokens[++i], NumberStyles.None, CultureInfo.InvariantCulture, out repeat) || repeat <= 0)
                    throw Invalid("InvalidDeltaRepeat");
                i++;
            }
            if (repeat > count - result.Count) throw Invalid("TextDeltaExpansion");
            float delta = Number(tokens[i]);
            for (int j = 0; j < repeat; j++) result.Add(delta);
        }
        if (result.Count < count - 1) throw Unsupported(OfdStrings.Get("ShortTextDeltas"));
        return result.ToArray();
    }
}
