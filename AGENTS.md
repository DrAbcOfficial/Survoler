# Project Guidance

## Product Scope

Survoler is an Android app for fast, fully local document previews. Rendering is
best-effort, not a guarantee of accuracy, completeness, or document authenticity.
Keep README.md concise; document detailed compatibility limits in FORMAT_SUPPORT.md.

- Keep document processing offline. Do not add uploads, remote conversion, telemetry,
  or runtime network dependencies. Release builds must not request INTERNET.
- Preview inputs read-only. Never execute document macros, add-ins, or scripts.
- Do not claim signature verification. Preserve visible warnings when content is skipped.
- Preserve the content-URI Open With entry point, no launcher entry, and ARM64 target.

## Structure

- `Survoler/`: shared .NET 10 / Avalonia UI, view models, input handling and conversion.
- `Survoler/Documents/`: extension classification, bounded input copies and sessions.
- `Survoler/Rendering/`: Office/CSV/OFD to PDF, PDF preview ownership and text maps.
- `Survoler.Android/`: activation, native PDF rendering, font resources and copy menu.
- `Survoler.Tests/`: MSTest tests and fixtures.
- `Directory.Packages.props`: centrally managed package versions.
- `build/Verify-AndroidPackage.ps1`: Release package, manifest and ABI checks.

Keep the pipeline unified: source format -> PDF -> existing PDF preview UI.
PDF inputs bypass conversion. Do not introduce a separate viewer per format
without a concrete requirement.

## Implementation Rules

- Prefer small, focused changes and existing abstractions. Keep Android APIs in
  the Android project and expose platform behavior through shared contracts.
- Preserve source files. Session copies and preview-owned PDFs have independent
  lifetimes; clean up owned temporary files on failure, cancellation and disposal.
- Preserve input, ZIP/XML, font, image, object and output budgets. Validate paths,
  disable external XML resolution, and check cancellation during bounded work.
- OFD deliberately supports a subset. Skip only explicitly permitted independent
  signature/annotation references with a persistent warning; do not silently drop
  unsupported body graphics or replace failed pages with incomplete images.
- When rich OFD conversion encounters unsupported features, an explicitly labeled
  text-only PDF may reflow extractable Unicode with a persistent warning. Never
  present it as the original layout or use it to bypass package/security limits.
- Keep PDF font/image resources document-scoped. Do not share mutable native font
  state across conversions; concurrency tests cover prior ToUnicode corruption.
- When adding formats, update classification, content validation, conversion,
  Android MIME filters, package verification, tests and FORMAT_SUPPORT.md together.
- Match direct SkiaSharp references to the Avalonia-managed/native dependency graph.
  Review licensing, Android ARM64 assets and Release trimming before adding libraries.
- Preserve unrelated worktree changes. Commit or deploy only when requested, and
  never commit build outputs, temporary documents or third-party research clones.

## Verification

Use the SDK pinned in `global.json`. Android builds also require the matching
.NET Android workload and Android SDK. Run commands from the repository root:

```powershell
dotnet test Survoler.Tests/Survoler.Tests.csproj
dotnet build Survoler.Android/Survoler.Android.csproj
& "./build/Verify-AndroidPackage.ps1" -Configuration Release
git diff --check
```

Use Release for package policy checks; Debug may include development-only network
permissions. Tests and successful builds do not establish real-device rendering
fidelity. Report device-testing gaps and unsupported features explicitly.

For documentation-only changes, check paths, supported-format claims and the diff;
application builds are not normally necessary.
