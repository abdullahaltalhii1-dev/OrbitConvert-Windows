# OrbitConvert

OrbitConvert is a Windows desktop application that will provide local file conversion workflows. The current repository contains only the Phase 0 foundation: a launchable WPF shell, basic MVVM binding, shared theme resources, tests, and CI.

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

The startup window displays `OrbitConvert` and `Phase 0 / Foundation ready`. It intentionally contains no file selection or conversion features.

## Structure

- `src/OrbitConvert.App` — WPF desktop executable and presentation code
- `tests/OrbitConvert.Tests` — xUnit tests
- `docs/architecture.md` — architecture and phase-boundary notes
- `.github/workflows/ci.yml` — Windows restore, Release build, and test workflow

## Scope

Phase 0 establishes a small, maintainable base. File selection, drag-and-drop, converters, Explorer integration, installers, and other later capabilities are explicitly out of scope until approved in separate milestones.
