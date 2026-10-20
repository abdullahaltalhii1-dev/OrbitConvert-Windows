# OrbitConvert

OrbitConvert is a Windows desktop application that will provide local file conversion workflows. The current repository contains the Phase 1.3 bilingual Studio UI shell plus the small Phase 2A image-engine foundation: complete English and Arabic resources, runtime LTR/RTL switching, native multi-file selection, in-app drag-and-drop, basic local file metadata, isolated image inspection, tests, and CI.

## Requirements

- Windows 11 or another supported Windows version
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2026 with the .NET desktop development workload is optional

## Build and test

From the repository root:

```powershell
dotnet restore OrbitConvert.slnx
dotnet build OrbitConvert.slnx -c Release --no-restore
dotnet test OrbitConvert.slnx -c Release --no-build
```

## Run

```powershell
dotnet run --project src/OrbitConvert.App/OrbitConvert.App.csproj
```

The startup window opens the Studio workspace. Browse Files uses the native Windows picker, and individual files can be dropped into the workspace. The selection remains in memory and can be edited or cleared. Settings can switch the entire interface between English and Arabic immediately; the language preference is stored at `%LOCALAPPDATA%\OrbitConvert\language.txt`. Theme and accent choices remain session-only.

## Structure

- `src/OrbitConvert.App` — WPF desktop executable and presentation code
- `tests/OrbitConvert.Tests` — xUnit tests
- `docs/architecture.md` — architecture and phase-boundary notes
- `docs/image-engine-decision.md` — pinned Magick.NET package and Q8/Q16 assessment
- `.github/workflows/ci.yml` — Windows restore, Release build, and test workflow

## Screenshots

- `docs/screenshots/01-dark-workspace.png`
- `docs/screenshots/02-light-workspace.png`
- `docs/screenshots/03-images-demo-preview.png`
- `docs/screenshots/04-settings-theme-accent.png`
- `docs/screenshots/05-file-intake-dark.png`
- `docs/screenshots/06-file-intake-light.png`
- `docs/screenshots/07-arabic-dark-workspace.png`
- `docs/screenshots/08-arabic-light-workspace.png`
- `docs/screenshots/09-arabic-settings-language.png`
- `docs/screenshots/10-arabic-selected-files.png`
- `docs/screenshots/11-english-workspace-after-switch.png`

## Scope

Phase 2A adds an isolated Magick.NET Q8 adapter that can inspect a caller-provided image stream. It is not connected to selected files or the UI. The application does not convert images, create output files, persist selections, scan folders, integrate with Explorer, or expose conversion actions. Those capabilities remain gated behind later approved milestones, and Convert remains disabled.
