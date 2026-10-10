param(
    [switch]$Test,
    [string]$Version,
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot

if (-not $PSBoundParameters.ContainsKey('Version')) {
    $versionFile = Join-Path $projectRoot 'VERSION'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw "Version file was not found: $versionFile"
    }
    $Version = [System.IO.File]::ReadAllText($versionFile).TrimEnd("`r", "`n")
}

$versionMatch = [System.Text.RegularExpressions.Regex]::Match(
    $Version,
    '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z'
)
if (-not $versionMatch.Success) {
    throw "Invalid version '$Version'. Use canonical MAJOR.MINOR.PATCH (for example 0.1.0)."
}

foreach ($component in @($versionMatch.Groups[1].Value, $versionMatch.Groups[2].Value, $versionMatch.Groups[3].Value)) {
    $componentValue = [int]0
    $parsed = [int]::TryParse(
        $component,
        [System.Globalization.NumberStyles]::None,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$componentValue
    )
    if (-not $parsed -or $componentValue -gt 65534) {
        throw "Invalid version '$Version'. Each version component must be between 0 and 65534."
    }
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler was not found.' }

$outputDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
$outputDir = [System.IO.Path]::GetFullPath($outputDir)
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$assemblyInfoPath = Join-Path $outputDir 'AssemblyInfo.cs'
$assemblyMetadata = @"
using System.Reflection;

[assembly: AssemblyTitle("Duo Click")]
[assembly: AssemblyProduct("Duo Click")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@
[System.IO.File]::WriteAllText($assemblyInfoPath, $assemblyMetadata, [System.Text.UTF8Encoding]::new($false))

$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'No C# source files found.' }
$exe = Join-Path $outputDir 'DuoClick.exe'
$iconPath = Join-Path $projectRoot 'assets\duoclick.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw 'Application icon is missing.' }
$compileSources = @($sources) + @($assemblyInfoPath)
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 "/out:$exe" "/win32manifest:$projectRoot\app.manifest" "/win32icon:$iconPath" "/resource:$iconPath,DuoClick.AppIcon.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll @compileSources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'DuoClick.exe.config') -Destination ($exe + '.config') -Force
Write-Host "Built $exe ($Version)"

if ($Test) {
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    $testExe = Join-Path $outputDir 'DuoClick.Tests.exe'
    $testSources = @($sources) + @($testFiles) + @($assemblyInfoPath)
    & $compiler /nologo /target:exe /main:DuoClick.Tests.TestRunner "/out:$testExe" "/win32manifest:$projectRoot\app.manifest" "/resource:$iconPath,DuoClick.AppIcon.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$packageFiles = @(
    $exe,
    ($exe + '.config'),
    (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'README.zh-CN.md'),
    (Join-Path $projectRoot 'assets\logo.png')
)
$archiveName = "DuoClick-$Version-Windows.zip"
$archivePath = Join-Path $outputDir $archiveName
$checksumPath = "$archivePath.sha256"
foreach ($staleArtifact in @($archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $staleArtifact -PathType Leaf) {
        Remove-Item -LiteralPath $staleArtifact -Force
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = $null
try {
    $archive = [System.IO.Compression.ZipFile]::Open(
        $archivePath,
        [System.IO.Compression.ZipArchiveMode]::Create
    )
    foreach ($packageFile in $packageFiles) {
        $entryName = [System.IO.Path]::GetFileName($packageFile)
        if ($entryName -eq 'logo.png') { $entryName = 'assets/logo.png' }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $packageFile,
            $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal
        )
    }
}
catch {
    if ($null -ne $archive) {
        $archive.Dispose()
        $archive = $null
    }
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    throw
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
}

$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$checksumText = "$checksum  $archiveName`r`n"
[System.IO.File]::WriteAllText($checksumPath, $checksumText, [System.Text.UTF8Encoding]::new($false))
Write-Host "Packaged $archivePath"
Write-Host "SHA256 $checksum"
