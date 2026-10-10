param(
    [string]$Version,
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# This script installs and uninstalls software. It is intentionally restricted
# to disposable GitHub-hosted runners and must never be invoked as a local test.
if ($env:GITHUB_ACTIONS -cne 'true') {
    throw 'Installer smoke tests may run only when GITHUB_ACTIONS is exactly true.'
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'RUNNER_TEMP is required for the GitHub Actions installer smoke test.'
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PSBoundParameters.ContainsKey('Version')) {
    $versionFile = Join-Path $projectRoot 'VERSION'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw "Version file was not found: $versionFile"
    }
    $Version = [System.IO.File]::ReadAllText($versionFile).TrimEnd("`r", "`n")
}
if ($Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw "Invalid version '$Version'. Use canonical MAJOR.MINOR.PATCH."
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}
$installerPath = Join-Path $outputPath "DuoClick-$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer was not found: $installerPath"
}

$runnerTemp = [System.IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/')
$installDirectory = [System.IO.Path]::GetFullPath((Join-Path $runnerTemp 'DuoClick Smoke - 测试 安装'))
$installPrefix = $runnerTemp + [System.IO.Path]::DirectorySeparatorChar
if (-not $installDirectory.StartsWith($installPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Computed install directory is outside RUNNER_TEMP: $installDirectory"
}
if (Test-Path -LiteralPath $installDirectory) {
    throw "Smoke install directory already exists: $installDirectory"
}

$settingsDirectory = Join-Path $env:LOCALAPPDATA 'DuoClick'
$runSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'DuoClick'
$uninstallSubkey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C2A64A4F-1E4E-4559-9A50-3A23B91F7A2B}_is1'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DuoClick.lnk'
$registryView = [Microsoft.Win32.RegistryView]::Registry32
$logDirectory = Join-Path $outputPath 'installer-logs'
$logPrefix = 'DuoClick-installer-smoke-' + $env:GITHUB_RUN_ID + '-' + $env:GITHUB_RUN_ATTEMPT

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-CurrentUserRegistryValue {
    param([string]$Subkey, [string]$Name)
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $registryView)
    try {
        $key = $baseKey.OpenSubKey($Subkey, $false)
        if ($null -eq $key) { return $null }
        try {
            if ($key.GetValueNames() -notcontains $Name) { return $null }
            return [pscustomobject]@{
                Value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                Kind = $key.GetValueKind($Name).ToString()
                View = $registryView
            }
        } finally {
            $key.Dispose()
        }
    } finally {
        $baseKey.Dispose()
    }
}

function Get-CurrentUserUninstallValue {
    param([string]$Name)
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $registryView)
    try {
        $key = $baseKey.OpenSubKey($uninstallSubkey, $false)
        if ($null -eq $key) { return $null }
        try {
            if ($key.GetValueNames() -notcontains $Name) { return $null }
            return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } finally {
            $key.Dispose()
        }
    } finally {
        $baseKey.Dispose()
    }
}

function Test-CurrentUserRegistryKey {
    param([string]$Subkey)
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $registryView)
    try {
        $key = $baseKey.OpenSubKey($Subkey, $false)
        if ($null -eq $key) { return $false }
        try {
            return $true
        } finally {
            $key.Dispose()
        }
    } finally {
        $baseKey.Dispose()
    }
}

function Assert-StartupDisabled {
    $entry = Get-CurrentUserRegistryValue -Subkey $runSubkey -Name $runValueName
    Assert-Condition ($null -eq $entry) "HKCU Run value '$runValueName' should be absent."
}

function Assert-StartupEnabled {
    $entry = Get-CurrentUserRegistryValue -Subkey $runSubkey -Name $runValueName
    $expected = '"' + (Join-Path $installDirectory 'DuoClick.exe') + '"'
    Assert-Condition ($null -ne $entry) "HKCU Run value '$runValueName' is missing."
    Assert-Condition ($entry.Kind -eq 'String') "HKCU Run value '$runValueName' must be REG_SZ."
    Assert-Condition ($entry.Value -ceq $expected) "HKCU Run value should be exactly $expected but was '$($entry.Value)'."
}

function Test-ByteArrayEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Normalize-DirectoryPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Assert-ApplicationFiles {
    $expectedFiles = @(
        'DuoClick.exe',
        'DuoClick.exe.config',
        'README.md',
        'README.zh-CN.md',
        'assets\logo.png'
    )
    foreach ($name in $expectedFiles) {
        $path = Join-Path $installDirectory $name
        Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Installed file is missing: $path"
        Assert-Condition ((Get-Item -LiteralPath $path).Length -gt 0) "Installed file is empty: $path"
    }

    $exePath = Join-Path $installDirectory 'DuoClick.exe'
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    Assert-Condition ($versionInfo.FileVersion -ceq "$Version.0") "Executable FileVersion '$($versionInfo.FileVersion)' does not match '$Version.0'."
    Assert-Condition ($versionInfo.ProductVersion -ceq $Version) "Executable ProductVersion '$($versionInfo.ProductVersion)' does not match '$Version'."

    Assert-Condition (Test-Path -LiteralPath $shortcutPath -PathType Leaf) "Start menu shortcut is missing: $shortcutPath"
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcutTarget = [System.IO.Path]::GetFullPath($shortcut.TargetPath)
        Assert-Condition ($shortcutTarget -ieq [System.IO.Path]::GetFullPath($exePath)) "Shortcut target '$shortcutTarget' does not point to '$exePath'."
    } finally {
        if ($null -ne $shortcut) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
        if ($null -ne $shell) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    }

    $displayVersion = Get-CurrentUserUninstallValue -Name 'DisplayVersion'
    $installLocation = Get-CurrentUserUninstallValue -Name 'InstallLocation'
    $uninstallString = Get-CurrentUserUninstallValue -Name 'UninstallString'
    Assert-Condition ($displayVersion -ceq $Version) "HKCU uninstall DisplayVersion '$displayVersion' does not match '$Version'."
    Assert-Condition ((Normalize-DirectoryPath $installLocation) -ieq (Normalize-DirectoryPath $installDirectory)) "HKCU uninstall InstallLocation '$installLocation' does not match '$installDirectory'."
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($uninstallString)) 'HKCU uninstall registration has no UninstallString.'
}

function Invoke-Installer {
    param([string]$Phase, [string[]]$TaskArguments)
    $logPath = Join-Path $logDirectory "$logPrefix-$Phase.log"
    if (Test-Path -LiteralPath $logPath) {
        throw "Installer log already exists: $logPath"
    }
    $arguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        ('/DIR="' + $installDirectory + '"'),
        ('/LOG="' + $logPath + '"')
    ) + $TaskArguments

    # Start-Process in Windows PowerShell 5.1 joins ArgumentList entries with
    # spaces; preserve quotes around values containing spaces and Unicode.
    $process = Start-Process -FilePath $installerPath -ArgumentList ($arguments -join ' ') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        $logText = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($logPath)
        } else {
            '<installer log was not created>'
        }
        throw "Installer phase '$Phase' exited with code $($process.ExitCode). Log: $logPath`r`n$logText"
    }
    Assert-Condition (Test-Path -LiteralPath $logPath -PathType Leaf) "Installer phase '$Phase' did not create its log: $logPath"
}

function Assert-SettingsBytes {
    param([byte[]]$LanguageBytes, [byte[]]$CaptureLimitBytes, [string]$Phase)
    $languagePath = Join-Path $settingsDirectory 'language.txt'
    $captureLimitPath = Join-Path $settingsDirectory 'capture-limit.txt'
    Assert-Condition (Test-Path -LiteralPath $languagePath -PathType Leaf) "$Phase removed language settings."
    Assert-Condition (Test-Path -LiteralPath $captureLimitPath -PathType Leaf) "$Phase removed capture-limit settings."
    $actualLanguage = [System.IO.File]::ReadAllBytes($languagePath)
    $actualCaptureLimit = [System.IO.File]::ReadAllBytes($captureLimitPath)
    Assert-Condition (Test-ByteArrayEqual -Left $actualLanguage -Right $LanguageBytes) "$Phase changed language settings bytes."
    Assert-Condition (Test-ByteArrayEqual -Left $actualCaptureLimit -Right $CaptureLimitBytes) "$Phase changed capture-limit settings bytes."
}

function Invoke-RegisteredUninstaller {
    param([string]$Phase)
    $uninstallString = [string](Get-CurrentUserUninstallValue -Name 'QuietUninstallString')
    if ([string]::IsNullOrWhiteSpace($uninstallString)) {
        $uninstallString = [string](Get-CurrentUserUninstallValue -Name 'UninstallString')
    }
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($uninstallString)) 'Cannot find the registered uninstaller.'

    $match = [System.Text.RegularExpressions.Regex]::Match($uninstallString, '^\s*"(?<path>[^"]+)"')
    Assert-Condition $match.Success "Uninstall command is not quoted as expected: $uninstallString"
    $uninstallerPath = [System.IO.Path]::GetFullPath($match.Groups['path'].Value)
    $installPrefix = [System.IO.Path]::GetFullPath($installDirectory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    Assert-Condition ($uninstallerPath.StartsWith($installPrefix, [System.StringComparison]::OrdinalIgnoreCase)) "Registered uninstaller is outside the smoke install directory: $uninstallerPath"
    Assert-Condition (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) "Registered uninstaller is missing: $uninstallerPath"

    $logPath = Join-Path $logDirectory "$logPrefix-uninstall-$Phase.log"
    if (Test-Path -LiteralPath $logPath) {
        throw "Uninstaller log already exists: $logPath"
    }
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $logPath + '"'
    $process = Start-Process -FilePath $uninstallerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        $logText = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($logPath)
        } else {
            '<uninstaller log was not created>'
        }
        throw "Uninstall phase '$Phase' exited with code $($process.ExitCode). Log: $logPath`r`n$logText"
    }
    Assert-Condition (Test-Path -LiteralPath $logPath -PathType Leaf) "Uninstall phase '$Phase' did not create its log: $logPath"
}

function Assert-AppNotRunning {
    $runningApps = @(Get-Process -Name 'DuoClick' -ErrorAction SilentlyContinue)
    Assert-Condition ($runningApps.Count -eq 0) 'DuoClick is running; refusing to continue or interfere with a live app.'
}

$fixtureOwned = $false
$installOwned = $false
$languageBytes = [System.Text.UTF8Encoding]::new($false).GetBytes('zh-CN')
$captureLimitBytes = [System.Text.UTF8Encoding]::new($false).GetBytes('7')

try {
    Assert-AppNotRunning

    Assert-Condition (-not (Test-Path -LiteralPath $settingsDirectory)) "Settings fixture already exists; refusing to overwrite: $settingsDirectory"
    Assert-Condition (-not (Test-Path -LiteralPath $shortcutPath)) "Start menu shortcut already exists; refusing to overwrite: $shortcutPath"
    Assert-Condition ($null -eq (Get-CurrentUserRegistryValue -Subkey $runSubkey -Name $runValueName)) "HKCU Run value '$runValueName' already exists; refusing to overwrite it."
    Assert-Condition (-not (Test-CurrentUserRegistryKey -Subkey $uninstallSubkey)) 'DuoClick uninstall registration already exists; refusing to overwrite it.'

    if (-not (Test-Path -LiteralPath $logDirectory)) {
        [void][System.IO.Directory]::CreateDirectory($logDirectory)
    }

    # Initial installation explicitly leaves startup disabled.
    $installOwned = $true
    Invoke-Installer -Phase 'initial-off' -TaskArguments @('/TASKS=')
    Assert-AppNotRunning
    Assert-ApplicationFiles
    Assert-StartupDisabled
    Assert-Condition (-not (Test-Path -LiteralPath $settingsDirectory)) 'Installer created user settings before the app was launched.'

    # Seed the production settings location and ensure setup reinstalls preserve
    # these user-owned bytes. This same-version reinstall is a repair/update-path
    # smoke only; no older-version migration is exercised here.
    [void][System.IO.Directory]::CreateDirectory($settingsDirectory)
    $fixtureOwned = $true
    [System.IO.File]::WriteAllBytes((Join-Path $settingsDirectory 'language.txt'), $languageBytes)
    [System.IO.File]::WriteAllBytes((Join-Path $settingsDirectory 'capture-limit.txt'), $captureLimitBytes)

    Invoke-Installer -Phase 'reinstall-on' -TaskArguments @('/TASKS=launchatlogin')
    Assert-AppNotRunning
    Assert-ApplicationFiles
    Assert-StartupEnabled
    Assert-SettingsBytes -LanguageBytes $languageBytes -CaptureLimitBytes $captureLimitBytes -Phase 'Startup-enabled reinstall'

    Invoke-Installer -Phase 'reinstall-off' -TaskArguments @('/TASKS=')
    Assert-AppNotRunning
    Assert-ApplicationFiles
    Assert-StartupDisabled
    Assert-SettingsBytes -LanguageBytes $languageBytes -CaptureLimitBytes $captureLimitBytes -Phase 'Startup-disabled reinstall'

    # Leave startup enabled for uninstall so the uninstaller's Run-key cleanup
    # is exercised directly.
    Invoke-Installer -Phase 'reinstall-uninstall-on' -TaskArguments @('/TASKS=launchatlogin')
    Assert-AppNotRunning
    Assert-ApplicationFiles
    Assert-StartupEnabled
    Assert-SettingsBytes -LanguageBytes $languageBytes -CaptureLimitBytes $captureLimitBytes -Phase 'Final startup-enabled reinstall'

    Invoke-RegisteredUninstaller -Phase 'final'
    Assert-AppNotRunning

    Assert-Condition ($null -eq (Get-CurrentUserUninstallValue -Name 'DisplayVersion')) 'Uninstall left the HKCU application registration behind.'
    Assert-StartupDisabled
    Assert-Condition (-not (Test-Path -LiteralPath $shortcutPath)) 'Uninstall left the Start menu shortcut behind.'
    Assert-Condition (-not (Test-Path -LiteralPath $installDirectory)) "Uninstall left application files in $installDirectory"
    Assert-SettingsBytes -LanguageBytes $languageBytes -CaptureLimitBytes $captureLimitBytes -Phase 'Uninstall'
    Write-Host "Installer smoke passed for DuoClick $Version. Same-version reinstall/startup toggles and settings preservation were verified."
} finally {
    # Best-effort cleanup only for paths this test confirmed absent before
    # creating. Preserve installer logs in the build output for CI diagnostics.
    $runningApps = @(Get-Process -Name 'DuoClick' -ErrorAction SilentlyContinue)
    if ($runningApps.Count -eq 0 -and $installOwned) {
        try {
            if ((Test-Path -LiteralPath $installDirectory) -and
                (Test-CurrentUserRegistryKey -Subkey $uninstallSubkey)) {
                Invoke-RegisteredUninstaller -Phase 'cleanup'
            }

            # Remove only the exact startup value and uninstall key created for
            # this smoke install if best-effort uninstallation left either one.
            $expectedRunValue = '"' + (Join-Path $installDirectory 'DuoClick.exe') + '"'
            $runEntry = Get-CurrentUserRegistryValue -Subkey $runSubkey -Name $runValueName
            if ($null -ne $runEntry -and $runEntry.Kind -eq 'String' -and $runEntry.Value -ceq $expectedRunValue) {
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $runEntry.View)
                try {
                    $key = $baseKey.OpenSubKey($runSubkey, $true)
                    if ($null -ne $key) {
                        try { $key.DeleteValue($runValueName, $false) } finally { $key.Dispose() }
                    }
                } finally {
                    $baseKey.Dispose()
                }
            }
            $registeredLocation = [string](Get-CurrentUserUninstallValue -Name 'InstallLocation')
            if (-not [string]::IsNullOrWhiteSpace($registeredLocation) -and
                (Normalize-DirectoryPath $registeredLocation) -ieq (Normalize-DirectoryPath $installDirectory)) {
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $registryView)
                try { $baseKey.DeleteSubKeyTree($uninstallSubkey, $false) } finally { $baseKey.Dispose() }
            }

            $currentInstallPath = [System.IO.Path]::GetFullPath($installDirectory)
            $tempPrefix = [System.IO.Path]::GetFullPath($runnerTemp).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            if ($currentInstallPath.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
                (Test-Path -LiteralPath $currentInstallPath)) {
                $installItem = Get-Item -LiteralPath $currentInstallPath -ErrorAction Stop
                if (-not ($installItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                    Remove-Item -LiteralPath $currentInstallPath -Recurse -Force -ErrorAction Stop
                }
            }
        } catch {
            Write-Warning "Best-effort smoke install cleanup failed: $($_.Exception.Message)"
        }
    }
    $runningApps = @(Get-Process -Name 'DuoClick' -ErrorAction SilentlyContinue)
    if ($runningApps.Count -eq 0 -and $fixtureOwned -and (Test-Path -LiteralPath $settingsDirectory)) {
        try {
            $languagePath = Join-Path $settingsDirectory 'language.txt'
            $capturePath = Join-Path $settingsDirectory 'capture-limit.txt'
            if ((Test-Path -LiteralPath $languagePath -PathType Leaf) -and
                (Test-Path -LiteralPath $capturePath -PathType Leaf) -and
                (Test-ByteArrayEqual -Left ([System.IO.File]::ReadAllBytes($languagePath)) -Right $languageBytes) -and
                (Test-ByteArrayEqual -Left ([System.IO.File]::ReadAllBytes($capturePath)) -Right $captureLimitBytes)) {
                Remove-Item -LiteralPath $languagePath -Force -ErrorAction Stop
                Remove-Item -LiteralPath $capturePath -Force -ErrorAction Stop
                if (@(Get-ChildItem -LiteralPath $settingsDirectory -Force).Count -eq 0) {
                    Remove-Item -LiteralPath $settingsDirectory -Force -ErrorAction Stop
                }
            }
        } catch {
            Write-Warning "Best-effort smoke settings fixture cleanup failed: $($_.Exception.Message)"
        }
    }
}
