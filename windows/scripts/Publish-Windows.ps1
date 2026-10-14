#Requires -Version 7.0
[CmdletBinding()]
param(
    # Relative output paths are resolved from the repository root, not the caller's directory.
    [string] $OutputDirectory = 'dist/windows'
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
$packageName = 'Compositor-Windows-win-x64'
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('compositor-publish-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stagingRoot $packageName
$temporaryZip = Join-Path $stagingRoot ($packageName + '.zip')
$archivePath = Join-Path $outputRoot ($packageName + '.zip')
$checksumPath = $archivePath + '.sha256'

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    # Enter the SDK-pinned directory. All other paths are absolute and the caller's directory is restored.
    Push-Location $windowsRoot
    try {
        $sdkVersion = & dotnet --version
        if ($LASTEXITCODE -ne 0) { throw 'The required .NET SDK is unavailable; see windows/global.json.' }
        # Build the CLI for this host, so Linux cross-publishing never executes a Windows binary.
        $hostCliRoot = Join-Path $stagingRoot 'host-cli'
        & dotnet build (Join-Path $windowsRoot 'src/Compositor.Cli/Compositor.Cli.csproj') `
            --configuration Release --output $hostCliRoot --nologo -warnaserror -m:1 -nr:false `
            -p:ContinuousIntegrationBuild=true -p:Deterministic=true
        if ($LASTEXITCODE -ne 0) { throw "Host CLI build failed with exit code $LASTEXITCODE." }
        $sampleRoot = Join-Path $packageRoot 'samples/Chinese-Acceptance'
        & dotnet (Join-Path $hostCliRoot 'Compositor.Cli.dll') sample $sampleRoot
        if ($LASTEXITCODE -ne 0) { throw "Acceptance sample generation failed with exit code $LASTEXITCODE." }
        & dotnet (Join-Path $hostCliRoot 'Compositor.Cli.dll') verify-sample $sampleRoot
        if ($LASTEXITCODE -ne 0) { throw "Acceptance sample verification failed with exit code $LASTEXITCODE." }

        # BuildServices runs AvaloniaStats only when this product list is nonempty. Keep the optional
        # collector disabled for packaging in addition to the documented telemetry opt-out environment.
        & dotnet publish (Join-Path $windowsRoot 'src/Compositor.Desktop/Compositor.Desktop.csproj') `
            --configuration Release --runtime win-x64 --self-contained true --output $packageRoot `
            --nologo -warnaserror -m:1 -nr:false -p:ContinuousIntegrationBuild=true -p:Deterministic=true `
            -p:UsedAvaloniaProducts= `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false `
            -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    } finally {
        Pop-Location
    }

    if (-not (Test-Path (Join-Path $packageRoot 'Compositor.Desktop.exe'))) {
        throw 'The published Windows executable is missing.'
    }
    Copy-Item (Join-Path $repositoryRoot 'LICENSE') (Join-Path $packageRoot 'LICENSE')
    Copy-Item (Join-Path $windowsRoot 'README.zh-CN.md') (Join-Path $packageRoot 'README.zh-CN.md')
    Copy-Item (Join-Path $windowsRoot 'README.md') (Join-Path $packageRoot 'README.upstream.md')
    Copy-Item (Join-Path $PSScriptRoot 'PORTABLE.zh-CN.txt') (Join-Path $packageRoot '使用说明.txt')
    $diagnosticsRoot = Join-Path $packageRoot 'diagnostics'
    New-Item -ItemType Directory -Path $diagnosticsRoot -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'Test-Windows.ps1') $diagnosticsRoot
    $diagnosticLauncher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Run-Diagnostics.cmd'))
    [IO.File]::WriteAllText((Join-Path $packageRoot 'Run-Diagnostics.cmd'),
        $diagnosticLauncher.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
    Copy-Item (Join-Path $windowsRoot 'docs/windows11-acceptance.zh-CN.md') (Join-Path $packageRoot 'Windows11-验收步骤.md')

    Copy-Item (Join-Path $windowsRoot 'docs/audit-fixes.zh-CN.md') (Join-Path $packageRoot '修复记录.md')

    # Self-contained publishing does not carry runtime or NuGet notices automatically.
    # Preserve the originals and package metadata instead of rewriting third-party license terms.
    $assetsPath = Join-Path $windowsRoot 'src/Compositor.Desktop/obj/project.assets.json'
    $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    $noticesRoot = Join-Path $packageRoot 'THIRD-PARTY-NOTICES'
    New-Item -ItemType Directory -Path $noticesRoot -Force | Out-Null
    foreach ($library in $assets['libraries'].GetEnumerator()) {
        if ($library.Value['type'] -ne 'package') { continue }
        foreach ($cacheRoot in $assets['packageFolders'].Keys) {
            $libraryRoot = Join-Path $cacheRoot $library.Value['path']
            if (-not (Test-Path $libraryRoot)) { continue }
            foreach ($relativePath in $library.Value['files']) {
                $name = [IO.Path]::GetFileName($relativePath)
                if ($name -notmatch '(?i)(license|notice|copyright|\.nuspec$)') { continue }
                $source = Join-Path $libraryRoot $relativePath
                $destination = Join-Path (Join-Path $noticesRoot $library.Value['path']) $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item $source $destination
            }
            break
        }
    }
    $runtimeConfig = Get-Content (Join-Path $packageRoot 'Compositor.Desktop.runtimeconfig.json') -Raw |
        ConvertFrom-Json -AsHashtable
    foreach ($framework in $runtimeConfig['runtimeOptions']['includedFrameworks']) {
        $runtimePackage = $framework['name'].ToLowerInvariant() + '.runtime.win-x64/' + $framework['version']
        $copiedRuntimeNotices = $false
        foreach ($cacheRoot in $assets['packageFolders'].Keys) {
            $runtimeRoot = Join-Path $cacheRoot $runtimePackage
            if (-not (Test-Path $runtimeRoot)) { continue }
            $destination = Join-Path $noticesRoot $runtimePackage
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item (Join-Path $runtimeRoot 'LICENSE.TXT') $destination
            Copy-Item (Join-Path $runtimeRoot 'THIRD-PARTY-NOTICES.TXT') $destination
            $copiedRuntimeNotices = $true
            break
        }
        if (-not $copiedRuntimeNotices) { throw "Could not find runtime notices for $runtimePackage." }
    }
    @(
        'Original license/notice files and NuGet metadata collected from restored build dependencies.'
        'This directory can include build-only packages and assets for other platforms.'
        'For each package, its .nuspec also records its authors, project URL and license declaration.'
    ) | Set-Content (Join-Path $noticesRoot 'README.txt') -Encoding utf8NoBOM

    $revision = 'unknown (source archive)'
    $sourceState = 'unknown (source archive or Git unavailable)'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $gitRevision = & git -C $repositoryRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $revision = $gitRevision
            $gitStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=normal 2>$null)
            if ($LASTEXITCODE -eq 0) {
                $sourceState = if ($gitStatus.Count -eq 0) { 'clean' } else { 'dirty (uncommitted changes)' }
            } else {
                $sourceState = 'unknown (git status failed)'
            }
        }
    }
    @(
        "Source revision: $revision"
        "Source working tree: $sourceState"
        "SDK: $sdkVersion"
        'Runtime: win-x64'
        'Configuration: Release, self-contained, untrimmed, multi-file'
    ) | Set-Content -Path (Join-Path $packageRoot 'BUILD-INFO.txt') -Encoding utf8NoBOM

    # Sort entries and fix ZIP timestamps so packaging does not add clock or file-order differences.
    # Identical published files produce identical archives; this is not a supply-chain reproducibility claim.
    $fileStream = [IO.File]::Create($temporaryZip)
    try {
        $zip = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            [string[]] $files = [IO.Directory]::GetFiles($packageRoot, '*', [IO.SearchOption]::AllDirectories)
            [Array]::Sort($files, [StringComparer]::Ordinal)
            foreach ($file in $files) {
                $relativePath = [IO.Path]::GetRelativePath($stagingRoot, $file).Replace('\', '/')
                $entry = $zip.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                $inputStream = [IO.File]::OpenRead($file)
                try { $inputStream.CopyTo($entryStream) } finally {
                    $inputStream.Dispose()
                    $entryStream.Dispose()
                }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fileStream.Dispose()
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Copy-Item $temporaryZip $archivePath -Force
    $hash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archivePath))" | Set-Content $checksumPath -Encoding utf8NoBOM
    Write-Output "Package: $archivePath"
    Write-Output "SHA-256: $checksumPath"
} finally {
    if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }
}
