using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Survoler.Resources;
using static Survoler.Rendering.OfdXml;

namespace Survoler.Rendering;

internal sealed partial class OfdPdfRenderer
{
    private void PaintPath(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope, byte alpha)
    {
        string data = Text(One(obj, "AbbreviatedData"));
        // Tokenize without accepting SVG syntax, implicit commands, or silently skipped garbage.
        Match match = Regex.Match(data, @"[SMLBQAC]|[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|\S", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        using var path = new SKPath();
        path.FillType = (obj.Attribute("Rule")?.Value ?? "NonZero") switch
        {
            "NonZero" => SKPathFillType.Winding,
            "Even-Odd" => SKPathFillType.EvenOdd,
            _ => throw Unsupported(OfdStrings.Get("PathFillRule"))
        };
        bool started = false;
        bool openSubpath = false;
        while (match.Success)
        {
            Tick();
            string command = Take();
            if (!openSubpath && command is not ("M" or "S")) throw Invalid("SubpathStart");
            switch (command)
            {
                case "M":
                    if (openSubpath) throw Unsupported(OfdStrings.Get("MoveInOpenSubpath"));
                    goto case "S";
                case "S": path.MoveTo(Next(), Next()); started = true; openSubpath = true; break;
                case "L": path.LineTo(Next(), Next()); break;
                case "B": path.CubicTo(Next(), Next(), Next(), Next(), Next(), Next()); break;
                case "Q": path.QuadTo(Next(), Next(), Next(), Next()); break;
                case "A":
                    float rx = Next(), ry = Next(), rotation = Next(), large = Next(), sweep = Next();
                    if (rx < 0 || ry < 0 || (large != 0 && large != 1) || (sweep != 0 && sweep != 1))
                        throw Invalid("InvalidArc");
                    path.ArcTo(rx, ry, rotation, large == 0 ? SKPathArcSize.Small : SKPathArcSize.Large,
                        sweep == 0 ? SKPathDirection.CounterClockwise : SKPathDirection.Clockwise, Next(), Next());
                    break;
                case "C": path.Close(); openSubpath = false; break;
                default: throw Unsupported(OfdStrings.Format("PathCommand", command));
            }
        }
        if (!started) throw Invalid("EmptyPath");
        using var paint = new SKPaint { IsAntialias = true };
        // Validate colors even when their corresponding operation is disabled.
        SKColor fill = Color(obj.Element(Ofd + "FillColor"), scope, alpha);
        SKColor stroke = Color(obj.Element(Ofd + "StrokeColor"), scope, alpha);
        paint.StrokeWidth = Positive(obj.Attribute("LineWidth")?.Value ?? "0.353");
        paint.StrokeCap = (obj.Attribute("Cap")?.Value ?? "Butt") switch
        { "Butt" => SKStrokeCap.Butt, "Round" => SKStrokeCap.Round, "Square" => SKStrokeCap.Square, _ => throw Unsupported(OfdStrings.Get("LineCap")) };
        paint.StrokeJoin = (obj.Attribute("Join")?.Value ?? "Miter") switch
        { "Miter" => SKStrokeJoin.Miter, "Round" => SKStrokeJoin.Round, "Bevel" => SKStrokeJoin.Bevel, _ => throw Unsupported(OfdStrings.Get("LineJoin")) };
        paint.StrokeMiter = Positive(obj.Attribute("MiterLimit")?.Value ?? "3.528");
        if (Boolean(obj, "Fill", false))
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Color = fill;
            canvas.DrawPath(path, paint);
        }
        if (Boolean(obj, "Stroke", true))
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.Color = stroke;
            canvas.DrawPath(path, paint);
        }
        return;

        float Next()
        {
            return Number(Take());
        }

        string Take()
        {
            if (!match.Success) throw Invalid("IncompletePathCommand");
            string value = match.Value;
            match = match.NextMatch();
            return value;
        }
    }

    private void PaintImage(SKCanvas canvas, XElement obj, Dictionary<string, Resource> scope, byte alpha)
    {
        _token.ThrowIfCancellationRequested();
        if (++_imageDraws > 10000) throw Invalid("ImageDrawBudget");
        Resource resource = Lookup(scope, Required(obj, "ResourceID"), "MultiMedia");
        string path = OfdPackage.Resolve(resource.BaseFile, Text(One(resource.Element, "MediaFile")));
        if (!_images.TryGetValue(path, out SKImage? image))
        {
            byte[] bytes = ReadAsset(path);
            bool png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            bool jpeg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            if (!png && !jpeg) throw Unsupported(OfdStrings.Get("ImageTypesSupported"));
            if (png)
            {
                // Some native PNG codecs expose only the first APNG frame.
                int offset = 8;
                while (offset < bytes.Length)
                {
                    Tick();
                    if (bytes.Length - offset < 12) throw Invalid("TruncatedPngChunk");
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                    if (length > bytes.Length - offset - 12) throw Invalid("InvalidPngChunkLength");
                    if (bytes.AsSpan(offset + 4, 4).SequenceEqual("acTL"u8)) throw Unsupported(OfdStrings.Get("AnimatedPng"));
                    offset += (int)length + 12;
                }
            }
            using SKData data = SKData.CreateCopy(bytes);
            using SKCodec codec = SKCodec.Create(data) ?? throw Invalid("InvalidImage");
            SKImageInfo info = codec.Info;
            long pixels = (long)info.Width * info.Height;
            if (info.Width <= 0 || info.Height <= 0 || pixels > 5000000)
                throw Invalid("ImagePixelLimit");
            if (pixels > 16000000 - _decodedPixels) throw Invalid("DecodedImageBudget");
            if (codec.FrameCount > 1) throw Unsupported(OfdStrings.Get("AnimatedImages"));
            if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft) throw Unsupported(OfdStrings.Get("ExifOrientation"));
            _token.ThrowIfCancellationRequested();
            _decodedPixels += pixels;
            using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw Invalid("ImageDecodeFailed");
            _token.ThrowIfCancellationRequested();
            // Immutable pixels can be retained by SKImage without a second decoded copy.
            bitmap.SetImmutable();
            image = SKImage.FromBitmap(bitmap) ?? throw Invalid("ImageRetainFailed");
            _images.Add(path, image);
        }
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
        // OFD images occupy the unit square; CTM supplies the physical placement and size.
        canvas.DrawImage(image, new SKRect(0, 0, 1, 1), new SKSamplingOptions(SKFilterMode.Linear), paint);
    }

    private static SKColor Color(XElement? element, Dictionary<string, Resource> scope, byte objectAlpha)
    {
        if (element is null) return SKColors.Black.WithAlpha(objectAlpha);
        Check(element, "Value ColorSpace Alpha", "");
        string[] values = Words(Required(element, "Value"));
        string type = element.Attribute("ColorSpace") is { } reference
            ? Required(Lookup(scope, reference.Value, "ColorSpace").Element, "Type") : "RGB";
        if (values.Length != (type == "GRAY" ? 1 : 3)) throw Invalid("ColorComponentCount");
        byte r = Byte(values[0]), g = type == "GRAY" ? r : Byte(values[1]), b = type == "GRAY" ? r : Byte(values[2]);
        byte alpha = Byte(element.Attribute("Alpha")?.Value ?? "255");
        return new SKColor(r, g, b, (byte)((alpha * objectAlpha + 127) / 255));
    }
}
