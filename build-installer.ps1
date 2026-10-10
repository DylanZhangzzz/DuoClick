param(
    [string]$Version,
    [string]$OutputDirectory = 'dist',
    [string]$CompilerPath
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

$outputDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $projectRoot $OutputDirectory
}
$outputDir = [System.IO.Path]::GetFullPath($outputDir)
$payloadFiles = @(
    (Join-Path $outputDir 'DuoClick.exe'),
    (Join-Path $outputDir 'DuoClick.exe.config'),
    (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'README.zh-CN.md'),
    (Join-Path $projectRoot 'assets\logo.png'),
    (Join-Path $projectRoot 'assets\duoclick.ico'),
    (Join-Path $projectRoot 'installer\DuoClick.iss'),
    (Join-Path $projectRoot 'installer\languages\ChineseSimplified.isl')
)
foreach ($payloadFile in $payloadFiles) {
    if (-not (Test-Path -LiteralPath $payloadFile -PathType Leaf)) {
        throw "Required installer input was not found: $payloadFile. Build the application first and check the installer assets."
    }
}

$exePath = Join-Path $outputDir 'DuoClick.exe'
$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$expectedFileVersion = "$Version.0"
if ($fileVersionInfo.FileVersion -ne $expectedFileVersion) {
    throw "Built executable version mismatch: expected FileVersion $expectedFileVersion, found '$($fileVersionInfo.FileVersion)'. Build the application with VERSION $Version first."
}
if ($fileVersionInfo.ProductVersion -ne $Version -and $fileVersionInfo.ProductVersion -ne $expectedFileVersion) {
    throw "Built executable version mismatch: expected ProductVersion $Version, found '$($fileVersionInfo.ProductVersion)'. Build the application with VERSION $Version first."
}

if ($PSBoundParameters.ContainsKey('CompilerPath')) {
    $compiler = $CompilerPath
    if (Test-Path -LiteralPath $compiler -PathType Container) {
        $compiler = Join-Path $compiler 'ISCC.exe'
    }
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "Inno Setup compiler was not found at CompilerPath '$CompilerPath'. Pass the full path to ISCC.exe or its installation directory."
    }
    $compiler = [System.IO.Path]::GetFullPath($compiler)
} else {
    $compilerCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) {
        $compiler = $compilerCommand.Source
    } else {
        $compilerCandidates = @()
        if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
            $compilerCandidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        }
        if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
            $compilerCandidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
        }
        $compiler = $compilerCandidates |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($compiler)) {
        throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6 on the build machine or pass -CompilerPath <path-to-ISCC.exe>. The build script does not install the compiler.'
    }
}

$installerPath = Join-Path $outputDir "DuoClick-$Version-Setup.exe"
$checksumPath = "$installerPath.sha256"
foreach ($staleArtifact in @($installerPath, $checksumPath)) {
    if (Test-Path -LiteralPath $staleArtifact -PathType Leaf) {
        Remove-Item -LiteralPath $staleArtifact -Force
    }
}

$environmentNames = @(
    'DUOCLICK_APP_VERSION',
    'DUOCLICK_PAYLOAD_DIR',
    'DUOCLICK_INSTALLER_DIR',
    'DUOCLICK_REPO_ROOT',
    'DUOCLICK_OUTPUT_DIR'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    [Environment]::SetEnvironmentVariable('DUOCLICK_APP_VERSION', $Version, 'Process')
    [Environment]::SetEnvironmentVariable('DUOCLICK_PAYLOAD_DIR', $outputDir, 'Process')
    [Environment]::SetEnvironmentVariable('DUOCLICK_INSTALLER_DIR', (Join-Path $projectRoot 'installer'), 'Process')
    [Environment]::SetEnvironmentVariable('DUOCLICK_REPO_ROOT', $projectRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DUOCLICK_OUTPUT_DIR', $outputDir, 'Process')

    $scriptPath = Join-Path $projectRoot 'installer\DuoClick.iss'
    & $compiler $scriptPath
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup reported success but did not create the expected installer: $installerPath"
}

$checksum = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$checksumText = "$checksum  $(Split-Path -Leaf $installerPath)`r`n"
[System.IO.File]::WriteAllText($checksumPath, $checksumText, [System.Text.UTF8Encoding]::new($false))
Write-Host "Packaged $installerPath"
Write-Host "SHA256 $checksum"
