# OrbitConvert

OrbitConvert is a Windows desktop application that will provide local file conversion workflows. The current repository contains the Phase 1.1 Studio UI shell: a responsive WPF interface, preview navigation, session-only appearance choices, a fictional image demo, tests, and CI.

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

The startup window opens the Studio workspace. Navigation, theme, accent, and fictional image output controls update presentation state in memory only. It intentionally contains no file selection or conversion features.

## Structure

- `src/OrbitConvert.App` — WPF desktop executable and presentation code
- `tests/OrbitConvert.Tests` — xUnit tests
- `docs/architecture.md` — architecture and phase-boundary notes
- `.github/workflows/ci.yml` — Windows restore, Release build, and test workflow

## Phase 1.1 screenshots

- `docs/screenshots/01-dark-workspace.png`
- `docs/screenshots/02-light-workspace.png`
- `docs/screenshots/03-images-demo-preview.png`
- `docs/screenshots/04-settings-theme-accent.png`

## Scope

Phase 1.1 establishes the approved visual shell. File selection, drag-and-drop, filesystem access, converters, Explorer integration, installers, persistence, and other later capabilities are explicitly out of scope until approved in separate milestones.
