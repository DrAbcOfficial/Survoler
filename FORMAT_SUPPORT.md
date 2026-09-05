# Office Format Support

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

## Boundaries

- These are extension aliases for compatible Microsoft binary content, not new
  proprietary-format parsers. Templates are previewed, not instantiated or saved.
- The open coordinator requires the complete OLE compound-file signature for
  these aliases. The corresponding OfficeIMO reader validates the document
  structure afterward; an OLE signature alone does not establish compatibility.
- Word requires supported Word binary content, Excel supported binary workbook
  content, and PowerPoint supported binary presentation content. Renaming an
  unrelated file does not convert it.
- Microsoft Works `.wps`, proprietary WPS variants, ZIP/OOXML, XML, RTF, and raw
  BIFF streams under these aliases are outside the supported scope. LibreOffice's
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
