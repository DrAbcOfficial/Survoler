$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$android = Join-Path $root "Survoler.Android/Survoler.Android.csproj"
$projects = @($android,
    (Join-Path $root "Survoler/Survoler.csproj"),
    (Join-Path $root "Survoler.Tests/Survoler.Tests.csproj"))

# Clear the CI override only in child MSBuild calls to exercise the source default.
$defaultOutput = & dotnet msbuild $android -nologo -p:ReleaseVersion= -getProperty:ApplicationDisplayVersion,ApplicationVersion,VersionPrefix
if ($LASTEXITCODE -ne 0) { throw "Default version evaluation failed: $defaultOutput" }
$default = ($defaultOutput -join "`n" | ConvertFrom-Json).Properties
$cases = @(
    @{ Version = $default.VersionPrefix; Code = $null; Override = "" },
    @{ Version = "0.0.0"; Code = "1"; Override = "0.0.0" },
    @{ Version = "0.1.0"; Code = "1001"; Override = "0.1.0" },
    @{ Version = "1.2.3"; Code = "1002004"; Override = "1.2.3" },
    @{ Version = "2099.999.999"; Code = "2100000000"; Override = "2099.999.999" }
)

foreach ($case in $cases) {
    $version = [Version]::Parse($case.Version)
    $expectedCode = [string]($version.Major * 1000000 + $version.Minor * 1000 + $version.Build + 1)
    if ($null -ne $case.Code -and $expectedCode -cne $case.Code) { throw "Incorrect version code test case." }
    foreach ($project in $projects) {
        $output = & dotnet msbuild $project -nologo "-p:ReleaseVersion=$($case.Override)" -t:ValidateVersion,GetAssemblyVersion -getProperty:ApplicationDisplayVersion,ApplicationVersion,VersionPrefix,AssemblyVersion
        if ($LASTEXITCODE -ne 0) { throw "Version validation failed for ${project}: $output" }
        $properties = ($output -join "`n" | ConvertFrom-Json).Properties
        if ($properties.VersionPrefix -cne $case.Version -or $properties.AssemblyVersion -cne "$($case.Version).0") {
            throw "Prefix/assembly version mismatch for ${project}: $output"
        }
        if ($project -eq $android -and ($properties.ApplicationDisplayVersion -cne $case.Version -or $properties.ApplicationVersion -cne $expectedCode)) {
            throw "Android version mismatch: $output"
        }
    }
    "Verified $($case.Version): Android code $expectedCode; SDK assembly versions match."
}

$invalidVersions = @("v0.0.2", "01.2.3", "1.02.3", "1.2.03", "1.2", "1.2.3.4", "1.2.3-beta", "1.2.3+build", "-1.2.3", "2100.0.0", "1.1000.0", "1.0.1000", "999999999999999999999.0.0", "1.2.3 ")
foreach ($version in $invalidVersions) {
    # PrepareForBuild proves the validation hook runs before compiler or dex work.
    $output = & dotnet msbuild $android -nologo "-p:ReleaseVersion=$version" -t:PrepareForBuild 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch "Invalid version '") {
        throw "Expected a clear validation error for '${version}': $output"
    }
}
"Verified rejection of $($invalidVersions.Count) malformed/out-of-range versions without Android compilation."
