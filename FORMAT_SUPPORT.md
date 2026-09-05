# Document Format Support

## PDF

PDF files (`.pdf`, case-insensitive) open directly through Android's native PDF
renderer, without Office conversion or rewriting PDF content. Android registers
`application/pdf` for content-URI activation.

- The input is copied into the existing size-limited session cache. An independent,
  byte-identical temporary PDF copy belongs to the preview, so closing/replacing
  an input session does not prematurely remove a PDF still used for rendering or
  text extraction. Disposing the preview removes its copy, never the source file.
- The coordinator requires `%PDF-` at byte zero; leading junk/BOM is not accepted.
  This is only a signature check. Native opening/rendering validates structure;
  malformed or unsupported PDFs may fail even with a valid header.
- Existing page navigation, pinch zoom, panning, and native copy menu are reused.
  Selection requires extractable PDF text supported by the interaction-map reader.
  Scanned/image-only PDFs can be displayed, but there is no OCR. Text extraction
  failure does not intentionally prevent page rendering.
- No password entry or decryption workflow is provided. Native security failures
  report that password-protected/restricted PDFs are unsupported. PDF JavaScript,
  form editing, annotations, embedded attachments, and signature verification are
  not implemented; this is a static preview, not an interactive PDF editor.
- Limits remain 64 MiB of input, 2,000 pages, a target render width up to 2,048
  pixels, and 5,000,000 pixels per page. These are not complete bounds on the
  decompressed complexity or execution time of an arbitrary PDF parser.

`PdfPreviewTests` checks headers, direct-conversion bypass, independent copy
lifetimes, cleanup on open/render failure and cancellation, and real text-map
extraction after input-session disposal. Page-renderer failures are mocked;
native rasterization and password-protected files still require device testing.

## Office Files

Survoler previews DOC/DOCX, XLS/XLSX, and PPT/PPTX through OfficeIMO's PDF
conversion pipeline. The following additional extensions use the existing
legacy binary readers:

| Extension | Reader | LibreOffice reference filter |
| --- | --- | --- |
| `.wps` | DOC | MS Word 97 |
| `.wpt` | DOC (template content) | MS Word 97 Vorlage |
| `.et` | XLS | MS Excel 97 |
| `.ett` | XLS (template content) | MS Excel 97 Vorlage/Template |
| `.dps` | PPT | MS PowerPoint 97 |
| `.dpt` | PPT (template content) | MS PowerPoint 97 Vorlage |

## Templates, Macro-Enabled Files, And Add-Ins

| Extension | Reader/container | Preview scope |
| --- | --- | --- |
| `.xlsm` | XLSX / ZIP | Static workbook content; VBA is not run |
| `.xlt` | XLS / OLE | Binary template content |
| `.xltm` | XLSX / ZIP | Macro-enabled template content; VBA is not run |
| `.dot` | DOC / OLE | Binary template content |
| `.dotx` | DOCX / ZIP | OpenXML template content |
| `.xla` | XLS / OLE | Best-effort static worksheet content only |
| `.xlam` | XLSX / ZIP | Best-effort static worksheet content only |
| `.pptm` | PPTX / ZIP | Static presentation content; VBA is not run |

Templates are opened read-only for preview, not instantiated. Add-ins are not
installed or executed: custom commands, VBA UDFs, automation, and macro-generated
content are unsupported. Add-ins containing only code or hidden/supporting sheets
may have no useful printable content or may fail conversion. A successful PDF does
not establish that an add-in works. Formula output may depend on cached values
and OfficeIMO's supported formula engine rather than Excel's runtime.

Android registers the macro-enabled whole-file MIME types (both `macroEnabled`
and lowercase `macroenabled`, because Android matching is case-sensitive) and the
Word OpenXML template MIME. XLT/XLA and DOT reuse the existing Excel and Word MIME
types. No generic MIME wildcard is added.

## Boundaries

### CSV

CSV is imported into a detached text-only workbook and rendered with the existing
spreadsheet PDF pipeline. It does not require an OLE/ZIP signature.

- Accepted encoding: strict UTF-8 with or without BOM, or UTF-16 LE/BE with BOM.
  Invalid bytes, UTF-32, and legacy encodings such as GBK are not auto-detected or
  silently replaced. Files in other encodings must be converted to UTF-8 first.
- The delimiter is a comma. Quoted commas, doubled quote escapes, multiline
  fields, empty fields, and trailing empty columns are supported. Blank lines are
  skipped; short rows remain short. Semicolons and tabs remain field text, not
  alternative delimiters. There is no locale-dependent delimiter guessing.
- Values stay text, preserving leading zeros, large identifiers, date-like
  strings, and formula-like text. CSV fields never become spreadsheet formulas.
  Logical field line breaks are preserved, with CRLF normalized in workbook XML.
- Cells use bounded column widths, wrapping, and automatic row heights. Extremely
  wide or long tables may span multiple PDF pages and are not guaranteed to match
  a desktop spreadsheet application's print layout.
- The existing 64 MiB input ceiling remains. CSV additionally permits at most
  10,000 nonblank records, 256 columns per record, 100,000 fields total, and 32,767
  characters per field. Exceeding a limit fails explicitly rather than truncating
  silently. Empty files, malformed quoted records, and XML-incompatible control
  characters produce an error.
- Android accepts `text/csv`, `text/comma-separated-values`, `application/csv`,
  and the existing Excel MIME type when the filename ends in `.csv`. Generic
  `text/plain` and wildcard MIME types are not claimed.

`CsvPreviewTests` covers parsing, Chinese decoding in all supported encodings,
literal cell types/no formulas, invalid input, resource limits, cancellation,
and CSV activation through readable/selectable PDF output without changing the
source. Legacy code pages and alternate-delimiter files are outside this scope.

### Binary And OpenXML

- These are extension aliases for compatible Microsoft binary content, not new
  proprietary-format parsers. Templates are previewed, not instantiated or saved.
- The open coordinator requires the complete OLE compound-file signature for
  legacy aliases and a ZIP signature for OpenXML aliases. The corresponding OfficeIMO reader validates the document
  structure afterward; an OLE signature alone does not establish compatibility.
- Word requires supported Word binary content, Excel supported binary workbook
  content, and PowerPoint supported binary presentation content. Renaming an
  unrelated file does not convert it.
- Microsoft Works `.wps`, proprietary WPS variants, ZIP/OOXML, XML, RTF, and raw
  BIFF streams under the WPS/legacy binary aliases are outside the supported scope. LibreOffice's
  Excel importer supports some raw BIFF inputs; Survoler deliberately retains its
  existing OLE-only legacy boundary.
- Existing input-size limits and read-only conversion policies remain unchanged.
  This change adds no password/decryption or active-content execution support.
- Android accepts the existing Microsoft MIME types plus `application/wps-office.*`
  and `application/x-*` entries for the six suffixes. It also accepts
  `application/vnd.ms-works`, which some providers associate with `.wps`; this is
  an activation hint, not a claim of Works parser support. Filename and content
  checks still apply. Generic or missing MIME types are not newly claimed, so
  visibility in a file manager's Open With list depends on its MIME reporting.

## Reference And Verification

Mappings were checked against a local sparse checkout of
[LibreOffice/core](https://github.com/LibreOffice/core) at commit
`8ce702c278f7c3f403602469e0cb38402c2b4375`.

Type definitions under `filter/source/config/fragments/types/`:

- `writer_MS_Word_97.xcu` and `writer_MS_Word_97_Vorlage.xcu`
- `calc_MS_Excel_97.xcu` and `calc_MS_Excel_97_VorlageTemplate.xcu`
- `impress_MS_PowerPoint_97.xcu` and `impress_MS_PowerPoint_97_Vorlage.xcu`
- `writer_MS_Works_Document.xcu` identifies the separate Works route.

Content boundaries were checked in `sw/source/filter/ww8/ww8par.cxx`,
`sc/source/filter/excel/excel.cxx`, and `sd/source/filter/ppt/pptin.cxx`.
No LibreOffice implementation code was copied or linked into Survoler.

Automated tests cover case-insensitive extension recognition, opening existing
OLE fixtures under all six aliases, rejecting non-OLE and truncated input, and
converting compatible alias-suffixed files into PDFs with extractable text.
These tests use renamed/generated Microsoft-format fixtures, not a corpus of
files created by different WPS Office versions; universal WPS compatibility or
template-rendering fidelity is not established by them.

`ExtendedOfficeFormatTests` additionally uses OpenXML SDK document types and
verifies actual main-part content types for XLSM, XLTM, DOTX, PPTM, and XLAM before
PDF conversion. Tests verify readable/selectable PDF text and unchanged input
bytes. These generated packages contain no VBA projects; real macro payloads and
VBA-only add-ins are not covered. The XLAM case retains ordinary worksheets.
Binary XLT/DOT/XLA tests currently use compatible XLS/DOC content under the new
extensions, not genuine template/add-in flags. Genuine binary add-ins and
template-specific fidelity remain unverified, especially for XLA.

Additional local LibreOffice references under the same `types/` directory:
`MS_Excel_2007_VBA_XML.xcu`, `MS_Excel_2007_XML_Template.xcu`,
`writer_MS_Word_2007_Template.xcu`, `writer_OOXML_Template.xcu`, and
`MS_PowerPoint_2007_XML_VBA.xcu`. No explicit XLA/XLAM registration was found there;
Survoler's limited add-in handling is based on its own converter tests, not a
claim of LibreOffice compatibility. Internal OOXML part content types are not
used as Android whole-file MIME types.
