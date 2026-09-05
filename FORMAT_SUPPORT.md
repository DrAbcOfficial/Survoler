# Document Format Support

## OFD

OFD (`.ofd`, case-insensitive) follows the same preview pipeline as Office files:
bounded OFD ZIP/XML parsing -> a temporary PDF -> the existing Android PDF viewer
and PDF text interaction map. Android registers `application/ofd` and
`application/vnd.ofd`. A ZIP signature alone is not enough: conversion requires
`OFD.xml`, the OFD 2016 namespace, and valid referenced document/page resources.

This is an explicitly restricted static-preview implementation, not full OFD
conformance. It uses the existing SkiaSharp 3.119.4 stack as a PDF writer, with a
direct package reference pinned to Avalonia's version. It does not add Docnet,
PDFium, PdfSharpCore, a WebView, or a separate OFD bitmap/selection backend. No
LibreOffice or ofdrw.net implementation source is incorporated.

Supported content:

- One DocBody, multiple pages with physical page boxes, page origins, document
  and page resources, and background/foreground templates with cycle detection.
- Filled Unicode text, font resources, TrueType font data, registered Android
  font substitutions/fallbacks, explicit or inherited TextCode positions, bounded
  DeltaX/DeltaY repetition, and CTM transformations. Text remains selectable in PDF.
  Missing glyphs fail conversion instead of silently drawing replacement boxes.
- Solid 8-bit RGB/Gray paths, lines, cubic/quadratic curves, elliptical arcs,
  closed subpaths, fill rules, basic cap/join styling, alpha and object transforms.
  `S` starts and initial `M` starts are accepted. An `M` within an unclosed
  subpath is rejected rather than guessing its closure semantics.
- PNG/JPEG images, unit-square image placement through CTM, alpha, and object
  Boundary clipping. Repeated image references reuse a decoded resource.
- Font and image objects have document-local lifetimes. In particular, the PDF
  writer does not share the native default typeface across concurrent conversions,
  avoiding the ToUnicode corruption reproduced by concurrency tests.

Independent DocBody `Signatures` and Document `Annotations` references are skipped
without resolving, reading, rendering or verifying their referenced content.
Missing or malformed overlay files therefore do not block an otherwise supported
page body. The preview displays a persistent partial-preview warning describing
the skipped content and stating that digital signatures have not been verified.
Signature/seal and annotation appearances will be absent. Ordinary page-body
images (including a seal already flattened into a body image) are not removed.
ZIP-level limits and the main XML safety checks still apply to the package.

Other unsupported content fails explicitly and deletes the incomplete PDF;
there is no image-only fallback that silently removes text or vector objects:

- Attachments, encryption, and multiple DocBody documents. A signed OFD can now
  preview its supported body, but skipping overlays does not enable unsupported
  body graphics or other document features. Showing or validating independent
  electronic seals is not claimed.
- CGTransform glyph mappings, composite objects, DrawParam resources/inheritance,
  gradients/patterns, arbitrary Clips, stroked text, unimplemented writing/shaping
  attributes and non-default image EXIF orientation.
- JBIG2, JPEG 2000, animated images, and other image codecs. Embedded CFF-only
  fonts are not supported. Complex script shaping is not implemented.
- Short explicit delta lists are rejected when they cannot provide every
  inter-character displacement; lists with one trailing displacement are allowed.
- Unknown XML visual elements/attributes are rejected rather than discarded.
  Compatibility with arbitrary producer-specific metadata is not guaranteed.

Safety budgets:

- 64 MiB input and generated PDF; 10,000 ZIP entries; 32 MiB per entry; 256 MiB
  declared expansion/cumulative reads; compression ratio at most 200.
- At most 8 MiB per XML source and 16 MiB cumulative XML source, depth 64,
  64 attributes per node and 200,000 XML nodes/attributes before DOM construction.
  DTDs and external XML resolution are disabled. Resource paths stay inside the
  archive; files are not extracted into caller-controlled filesystem paths.
- 2,000 pages, 100,000 processing/object operations, one million text characters,
  bounded delta expansion and finite coordinates. Physical pages are limited to
  14,400 PDF points on either axis.
- Five million pixels per decoded image, 16 million decoded pixels across cached
  images, 10,000 image draws and 64 MiB loaded font data. Limits do not make native
  font/image parsing fully interruptible or establish a security sandbox.

`OfdPreviewTests` generates packages and verifies PDF text, interaction-map
positions, transforms, path geometry, page sizes/origins, template order,
embedded/registered fonts, Unicode, image reuse, independent temporary-file
ownership, rejection limits, skipped overlay references/warning propagation and
concurrent PDF generation. These are not a
reference-image conformance suite or Android device tests; real signed/complex
OFD documents require further feature work and corpus validation.

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
