using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.VisualBasic.FileIO;
using OfficeIMO.Excel;
using Survoler.Documents;
using Survoler.Resources;

namespace Survoler.Rendering;

public static class CsvWorkbookReader
{
    public static ExcelDocument Load(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > PreviewLimits.MaxInputBytes)
        {
            throw new DocumentOpenException(Strings.Get("FileTooLarge"));
        }

        Span<byte> header = stackalloc byte[4];
        int count = stream.Read(header);
        Encoding encoding = new UTF8Encoding(false, true);
        int preamble = 0;
        if (count >= 4 &&
            ((header[0] == 0xFF && header[1] == 0xFE && header[2] == 0 && header[3] == 0) ||
             (header[0] == 0 && header[1] == 0 && header[2] == 0xFE && header[3] == 0xFF)))
        {
            throw new DocumentOpenException(Strings.Get("CsvEncoding"));
        }
        if (count >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            preamble = 3;
        }
        else if (count >= 2 && header[0] == 0xFF && header[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, false, true);
            preamble = 2;
        }
        else if (count >= 2 && header[0] == 0xFE && header[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, false, true);
            preamble = 2;
        }
        stream.Position = preamble;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
        ExcelDocument document = ExcelDocument.Create();
        try
        {
            using var parser = new TextFieldParser(reader)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };
            parser.SetDelimiters(",");
            ExcelSheet sheet = document.AddWorksheet("CSV");
            int rows = 0;
            int columns = 0;
            int cells = 0;
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[]? fields = parser.ReadFields();
                if (fields is null)
                {
                    break;
                }
                rows++;
                cells += fields.Length;
                if (rows > 10000 || fields.Length > 256 || cells > 100000)
                {
                    throw new DocumentOpenException(
                        Strings.Get("CsvLimits"));
                }
                columns = Math.Max(columns, fields.Length);
                for (int column = 0; column < fields.Length; column++)
                {
                    string field = fields[column];
                    if (field.Length > 32767)
                    {
                        throw new DocumentOpenException(Strings.Get("CsvFieldLimit"));
                    }
                    XmlConvert.VerifyXmlChars(field);
                    // Text setters preserve identifiers and never interpret CSV fields as formulas.
                    sheet.CellValue(rows, column + 1, field);
                    sheet.CellWrapText(rows, column + 1, true);
                }
            }
            if (rows == 0)
            {
                throw new DocumentOpenException(Strings.Get("CsvEmpty"));
            }
            for (int column = 1; column <= columns; column++)
            {
                sheet.SetColumnWidth(column, 24);
            }
            cancellationToken.ThrowIfCancellationRequested();
            sheet.AutoFitRows(ct: cancellationToken);
            return document;
        }
        catch (Exception exception)
        {
            document.Dispose();
            DocumentOpenException? failure = exception switch
            {
                DecoderFallbackException => new DocumentOpenException(Strings.Get("CsvEncoding")),
                MalformedLineException => new DocumentOpenException(Strings.Get("CsvMalformed")),
                XmlException => new DocumentOpenException(Strings.Get("CsvControlCharacters")),
                _ => null
            };
            if (failure is not null)
            {
                throw failure;
            }
            throw;
        }
    }
}
