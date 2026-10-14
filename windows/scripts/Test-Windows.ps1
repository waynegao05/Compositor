#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ApplicationPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Compositor.Desktop.exe'),
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this script on Windows. Use Test-Headless.ps1 for cross-platform development checks.'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
if (-not $OutputDirectory) {
    $runName = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Compositor/Checks/' + $runName)
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
# Refuse reuse so an earlier screenshot or archive cannot make a failed run appear successful.
if (Test-Path -LiteralPath $outputRoot) { throw "Choose a new output directory: $outputRoot" }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($ApplicationPath)
$system = [ordered]@{
    RecordedAtUtc = [DateTime]::UtcNow.ToString('o')
    ApplicationVersion = $version.ProductVersion
    FileVersion = $version.FileVersion
    WindowsVersion = [Environment]::OSVersion.VersionString
    Is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    VerificationScope = 'Automated headless checks on this Windows host; manual IME/DPI acceptance is separate.'
}
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -Property Caption, Version, BuildNumber, ProductType
    $system['WindowsCaption'] = $os.Caption
    $system['WindowsBuild'] = $os.BuildNumber
    $system['WindowsProductType'] = $os.ProductType
} catch {
    $system['WindowsDetails'] = 'Unavailable; basic OS version recorded above.'
}
$system | ConvertTo-Json | Set-Content (Join-Path $outputRoot 'system.json') -Encoding UTF8
$buildInfo = Join-Path (Split-Path -Parent $ApplicationPath) 'BUILD-INFO.txt'
if (Test-Path -LiteralPath $buildInfo) { Copy-Item -LiteralPath $buildInfo -Destination $outputRoot }
$results = [Collections.Generic.List[object]]::new()

function Invoke-Check {
    param([string] $Name, [string[]] $CommandArguments, [string] $ExpectedOutput, [switch] $AllowUnavailable)
    $stdout = Join-Path $outputRoot ($Name + '.log')
    $stderr = Join-Path $outputRoot ($Name + '.stderr.log')
    $status = [ordered]@{ Name = $Name; Passed = $false; Required = $true; ExitCode = $null; Error = $null }
    try {
        # Explicit quoting keeps generated output paths containing spaces together in Windows argv.
        $quotedArguments = @($CommandArguments | ForEach-Object { '"' + $_ + '"' })
        $process = Start-Process -FilePath $ApplicationPath -ArgumentList $quotedArguments -Wait -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $status['ExitCode'] = $process.ExitCode
        if ($AllowUnavailable -and $process.ExitCode -eq 3) {
            $status['Required'] = $false
            $status['Error'] = 'Optional camera RAW support is unavailable. Keep the complete package, install or repair Microsoft Visual C++ v14 Redistributable (x64), then restart Compositor.'
            Write-Output "WARN $Name : $($status['Error'])"
            Write-Output 'Official download: https://aka.ms/vc14/vc_redist.x64.exe'
            $results.Add([pscustomobject]$status)
            return
        }
        if ($process.ExitCode -ne 0) { throw "Process exited with code $($process.ExitCode)." }
        if ($ExpectedOutput -and (-not (Test-Path -LiteralPath $ExpectedOutput) -or (Get-Item -LiteralPath $ExpectedOutput).Length -eq 0)) {
            throw "Expected output is missing or empty: $ExpectedOutput"
        }
        $status['Passed'] = $true
        Write-Output "PASS $Name"
    } catch {
        $status['Error'] = $_.Exception.Message
        Write-Output "FAIL $Name : $($_.Exception.Message)"
    }
    $results.Add([pscustomobject]$status)
}

# Preserve the preceding real session before the checks create enough new sessions to rotate it out.
$priorDiagnosticZip = Join-Path $outputRoot 'before-checks-diagnostics.zip'
Invoke-Check -Name 'diagnostics-before-checks' -CommandArguments @('--diagnostics', $priorDiagnosticZip) -ExpectedOutput $priorDiagnosticZip

# Missing optional RAW support does not invalidate the basic PNG/JPEG editing checks.
Invoke-Check -Name 'native-dependencies' -CommandArguments @('--native-dependencies') -AllowUnavailable

foreach ($language in @('en', 'zh-Hans')) {
    foreach ($check in @('window', 'tabs', 'tools', 'shortcuts', 'dialogs', 'clicks', 'camera-raw', 'localization', 'ime', 'diagnostics-check', 'save-check', 'close-check', 'editing-check', 'import-check', 'recovery-check', 'clipboard-check', 'external-files-check', 'dialogs-check')) {
        $arguments = @("--$check", "--language=$language")
        $expectedOutput = $null
        if ($check -notin @('ime', 'diagnostics-check', 'save-check', 'close-check', 'editing-check', 'import-check', 'recovery-check', 'clipboard-check', 'external-files-check', 'dialogs-check')) {
            $expectedOutput = Join-Path $outputRoot ($language + '-' + $check + '.png')
            $arguments += $expectedOutput
        }
        Invoke-Check -Name ($language + '-' + $check) -CommandArguments $arguments -ExpectedOutput $expectedOutput
    }
}
$diagnosticZip = Join-Path $outputRoot 'diagnostics.zip'
Invoke-Check -Name 'diagnostics-export' -CommandArguments @('--diagnostics', $diagnosticZip) -ExpectedOutput $diagnosticZip
$results.ToArray() | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $outputRoot 'results.json') -Encoding UTF8
Write-Output "Results: $outputRoot"
Write-Output 'Manual Windows 11 checks: follow Windows11-acceptance instructions for IME, DPI, system image clipboard, local file drop, startup paths and interrupted-save recovery.'
if (@($results | Where-Object { $_.Required -and -not $_.Passed }).Count -gt 0) { exit 1 }
exit 0
