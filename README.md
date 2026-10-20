# OrbitConvert

OrbitConvert is a Windows desktop application that will provide local file conversion workflows. The current repository contains the Phase 1.2 file-intake experience on top of the approved Studio UI shell: native multi-file selection, in-app drag-and-drop, basic local file metadata, tests, and CI.

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

The startup window opens the Studio workspace. Browse Files uses the native Windows picker, and individual files can be dropped into the workspace. The selection remains in memory and can be edited or cleared. Navigation, theme, accent, and the fictional Images demo remain available.

## Structure

- `src/OrbitConvert.App` — WPF desktop executable and presentation code
- `tests/OrbitConvert.Tests` — xUnit tests
- `docs/architecture.md` — architecture and phase-boundary notes
- `.github/workflows/ci.yml` — Windows restore, Release build, and test workflow

## Screenshots

- `docs/screenshots/01-dark-workspace.png`
- `docs/screenshots/02-light-workspace.png`
- `docs/screenshots/03-images-demo-preview.png`
- `docs/screenshots/04-settings-theme-accent.png`
- `docs/screenshots/05-file-intake-dark.png`
- `docs/screenshots/06-file-intake-light.png`

## Scope

Phase 1.2 reads only a selected file's path, name, extension, and size. It does not open file contents, scan folders, convert files, persist selections, integrate with Explorer, or add conversion engines. Those capabilities remain gated behind later approved milestones.
