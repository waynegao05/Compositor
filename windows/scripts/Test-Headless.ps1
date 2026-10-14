#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'dist/windows/checks',
    [ValidateSet('window', 'tabs', 'tools', 'shortcuts', 'dialogs', 'clicks', 'camera-raw', 'localization', 'ime', 'diagnostics-check', 'save-check', 'close-check', 'editing-check', 'import-check', 'recovery-check', 'clipboard-check', 'external-files-check', 'dialogs-check')]
    [string[]] $Checks = @('window', 'tabs', 'tools', 'shortcuts', 'dialogs', 'clicks', 'camera-raw', 'localization', 'ime', 'diagnostics-check', 'save-check', 'close-check', 'editing-check', 'import-check', 'recovery-check', 'clipboard-check', 'external-files-check', 'dialogs-check'),
    # Omit this to run the previously built Release project, or supply a published executable to check it.
    [string] $ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $windowsRoot
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
if ($ApplicationPath) {
    $ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Push-Location $windowsRoot
try {
    foreach ($check in $Checks) {
        $png = Join-Path $outputRoot ($check + '.png')
        $log = Join-Path $outputRoot ($check + '.log')
        $needsScreenshot = $check -notin @('ime', 'diagnostics-check', 'save-check', 'close-check', 'editing-check', 'import-check', 'recovery-check', 'clipboard-check', 'external-files-check', 'dialogs-check')
        $checkArguments = @("--$check")
        if ($needsScreenshot) { $checkArguments += $png }
        # Do not accept an image left over from an earlier successful run.
        if (Test-Path $png) { Remove-Item $png -Force }
        if ($ApplicationPath) {
            & $ApplicationPath @checkArguments 2>&1 | Tee-Object -FilePath $log
        } else {
            & dotnet run --project (Join-Path $windowsRoot 'src/Compositor.Desktop/Compositor.Desktop.csproj') `
                --configuration Release --no-build -- @checkArguments 2>&1 | Tee-Object -FilePath $log
        }
        if ($LASTEXITCODE -ne 0) { throw "Headless --$check failed with exit code $LASTEXITCODE; see $log." }
        if ($needsScreenshot -and (-not (Test-Path $png) -or (Get-Item $png).Length -eq 0)) {
            throw "Headless --$check did not produce a screenshot: $png."
        }
    }
} finally {
    Pop-Location
}
