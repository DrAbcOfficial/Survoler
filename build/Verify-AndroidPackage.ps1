param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "Survoler.Android\Survoler.Android.csproj"
$framework = "net10.0-android36.0"
$runtime = "android-arm64"
$output = Join-Path $root "Survoler.Android\bin\$Configuration\$framework\$runtime"
$intermediate = Join-Path $root "Survoler.Android\obj\$Configuration\$framework\$runtime"
$manifestPath = Join-Path $intermediate "android\AndroidManifest.xml"
$apkPath = Join-Path $output "net.drabc.survoler-Signed.apk"

& dotnet publish $project -c $Configuration -f $framework -r $runtime
if ($LASTEXITCODE -ne 0) {
    throw "Android publish failed."
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Merged Android manifest was not found: $manifestPath"
}

if (-not (Test-Path -LiteralPath $apkPath)) {
    throw "Signed APK was not found: $apkPath"
}

$manifest = [System.IO.File]::ReadAllText($manifestPath)
$forbiddenManifestValues = @(
    "android.intent.action.MAIN",
    "android.intent.category.LAUNCHER",
    "android.permission.INTERNET"
)

foreach ($value in $forbiddenManifestValues) {
    if ($manifest.IndexOf($value, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Merged manifest contains forbidden value: $value"
    }
}

$requiredManifestValues = @(
    "android.intent.action.VIEW",
    "android.intent.category.DEFAULT",
    "application/msword",
    "text/csv",
    "text/comma-separated-values",
    "application/csv",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    "application/vnd.ms-excel",
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    "application/vnd.ms-powerpoint",
    "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
    "application/vnd.ms-excel.sheet.macroenabled.12",
    "application/vnd.ms-excel.template.macroenabled.12",
    "application/vnd.ms-excel.addin.macroenabled.12",
    "application/vnd.ms-powerpoint.presentation.macroenabled.12",
    "application/vnd.ms-excel.sheet.macroEnabled.12",
    "application/vnd.ms-excel.template.macroEnabled.12",
    "application/vnd.ms-excel.addin.macroEnabled.12",
    "application/vnd.ms-powerpoint.presentation.macroEnabled.12",
    "application/wps-office.wps",
    "application/wps-office.wpt",
    "application/wps-office.et",
    "application/wps-office.ett",
    "application/wps-office.dps",
    "application/wps-office.dpt",
    "application/vnd.ms-works",
    "application/x-wps",
    "application/x-wpt",
    "application/x-et",
    "application/x-ett",
    "application/x-dps",
    "application/x-dpt",
    'android:scheme="content"',
    'android:exported="true"',
    'android:icon=',
    'android:minSdkVersion="23"',
    'android:targetSdkVersion="36"'
)

foreach ($value in $requiredManifestValues) {
    if ($manifest.IndexOf($value, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Merged manifest is missing required value: $value"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($apkPath)
try {
    $nativeLibraries = @($archive.Entries | Where-Object { $_.FullName.StartsWith("lib/", [System.StringComparison]::Ordinal) })
    if ($nativeLibraries.Count -eq 0) {
        throw "APK does not contain native libraries."
    }

    $unexpectedLibraries = @($nativeLibraries | Where-Object {
        -not $_.FullName.StartsWith("lib/arm64-v8a/", [System.StringComparison]::Ordinal)
    })
    if ($unexpectedLibraries.Count -gt 0) {
        $paths = ($unexpectedLibraries | ForEach-Object { $_.FullName }) -join ", "
        throw "APK contains non-arm64 native libraries: $paths"
    }
}
finally {
    $archive.Dispose()
}

$apkSizeMiB = [Math]::Round((Get-Item -LiteralPath $apkPath).Length / 1MB, 2)
"Verified: $apkPath"
"APK size: $apkSizeMiB MiB"
"ABI: arm64-v8a only"
"Launcher: absent"
"Office MIME filters: present"
