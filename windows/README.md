# Compositor for Windows, Chinese community branch

This branch builds on the [Compositor](https://github.com/robbietilton/Compositor)
[community Windows port](https://github.com/chenguisen/Compositor/tree/compositor_win).
The Windows implementation uses C#, .NET 10, Avalonia and SkiaSharp. It targets Windows 11 x64 and
reads and writes the same `.comp` project format as the macOS implementation.
The tracked feature baseline is macOS 1.3.7; parity with newer upstream releases is not claimed.

See [the Chinese guide](README.zh-CN.md) for setup, diagnostics and native dependency details.
This is a community derivative under the MIT license, Copyright (c) 2026 Wonder Assembly LLC.

## Version 1.3.7-zh.5

This release addresses audit findings B01 through B39: consistent project reads during saves,
unfinished edits and undo boundaries, mask and nested group state, text and clipboard transactions,
adjustment controls and previews, TIFF alpha and orientation, export resolution and failure handling,
and Camera Raw layout. It also fixes group placement when a drag returns to its starting position.
Two macOS localization issues receive static source and resource corrections.

The [audit fix record](docs/audit-fixes.zh-CN.md) maps every finding to its implementation and regression
entry point. Refer to that record for executed results. The automation target is 18 modes in each of
English and Simplified Chinese, 36 runs in total. Native Windows 11 and macOS acceptance remains pending.

## Projects and setup

| Project | Purpose |
| --- | --- |
| `src/Compositor.Core` | Document model, rendering, editing, project I/O, import/export and undo |
| `src/Compositor.Desktop` | Avalonia editor and headless interaction checks |
| `src/Compositor.Cli` | Project manipulation, rendering and acceptance sample generation |
| `tests/Compositor.Core.Tests` | Core regression tests, including saved state, pixels and file safety |

Source builds require the .NET SDK specified by `global.json`, currently 10.0.401 with feature-band patch
roll-forward, and PowerShell 7 for the build scripts. Generating the Chinese acceptance sample requires
a font covering its Chinese characters. From the repository root:

```powershell
Set-Location windows
dotnet restore Compositor.slnx
dotnet build Compositor.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/Compositor.Core.Tests/Compositor.Core.Tests.csproj --configuration Release --no-build
dotnet run --project src/Compositor.Desktop --configuration Release --no-build
```

Use Edit > Language and restart to change the saved language preference. `--language=en` or
`--language=zh-Hans` overrides the preference for one launch. Local project and image paths can be supplied
as startup arguments or dragged into the window. Quote paths containing spaces.

## Portable Windows package

From the repository root:

```powershell
pwsh -File ./windows/scripts/Publish-Windows.ps1
```

The default output is `dist/windows/Compositor-Windows-win-x64.zip`, with a SHA-256 file beside it.
The package includes the .NET runtime, Chinese acceptance samples, diagnostics scripts, build information,
the audit fix record and license notices. Extract the whole archive before running `Compositor.Desktop.exe`.
Cross-publishing on Linux does not execute the Windows binary.

Camera RAW support additionally requires the x64 Microsoft Visual C++ v14 runtime. Check it with
`Compositor.Desktop.exe --native-dependencies`; see the Chinese guide for Microsoft's installation links.
The app and diagnostics do not install system components automatically.
There is no installer, code signature, file association registration or automatic Windows update channel.

## Automated checks

After the Release build, run from the repository root:

```powershell
pwsh -File ./windows/scripts/Test-Headless.ps1
```

The script runs 18 modes in the selected language. The CI workflow and portable package's
`diagnostics/Test-Windows.ps1` run both English and Simplified Chinese, targeting 36 mode runs.
The modes are:

```text
window, tabs, tools, shortcuts, dialogs, clicks, camera-raw, localization,
ime, diagnostics-check, save-check, close-check, editing-check, import-check,
recovery-check, clipboard-check, external-files-check, dialogs-check
```

`--dialogs-check` tests parameter bindings, preview callbacks, presets, guide validation and panel layout.
`--editing-check` includes pending interactions, history boundaries, text handling and lost pointer capture.
Screenshot modes show headless windows before capture so their templates and scroll content are laid out.
Clipboard checks use a simulated interface. File checks use temporary test copies.

Core test results, headless results, cross-publishing and native acceptance are separate evidence.
These checks cannot establish Windows IME candidate behavior, actual system clipboard interoperability,
monitor DPI transitions, or native file lock and disk failure behavior.
Follow the [Windows 11 acceptance guide](docs/windows11-acceptance.zh-CN.md) on a Windows 11 machine.

## Behavior and limits

- Project reads, saves and recovery use the same target lock. A busy operation times out after two seconds
  and can be retried. Recovery discovery skips busy candidates until a later scan.
- Recovery creates a new project from a valid, registered interrupted-save candidate. It does not provide
  periodic autosave or recover edits that were never saved.
- PNG and JPEG exports write a temporary file beside the destination, flush and close it, then replace the
  destination. PNG pHYs density is rounded to whole pixels per meter; JPEG JFIF density uses integer DPI.
- Resizing a rotated or flipped live shape preserves its rendered appearance by rasterizing it. Its shape
  parameters then cease to be editable; undo restores the preceding state.
- A pending image paste is discarded if its document, selection of layers or edit generation changes.
  Active canvas gestures, text sessions and previews block it from joining another edit's history.
- Chinese layout supports wrapping without spaces, common punctuation rules, font fallback and grapheme
  navigation. Full bidirectional layout, all emoji ligatures and IME surrounding-text reconversion remain
  outside the current text implementation.
- Apple Vision background removal, object selection and subject selection are absent. Bloom and Glow can
  differ from Core Image. Real camera files and macOS/Windows project round trips still require validation.

The macOS source is retained for reference and localization work. The Windows build does not compile it;
macOS source corrections require Xcode and native execution for runtime validation.

## License

MIT. Keep the repository's `LICENSE` and copyright notice when distributing this derivative.
The publish script collects available dependency licenses, notices, NuGet metadata and .NET runtime
license files into `THIRD-PARTY-NOTICES/`; third-party components retain their own licenses.
