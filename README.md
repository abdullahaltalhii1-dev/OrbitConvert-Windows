# OrbitConvert

OrbitConvert is a bilingual Windows desktop application for local file conversion. Phase 2B adds real single-image PNG ↔ JPEG conversion to the Studio shell, with safe output handling, Arabic/English localization, runtime RTL/LTR, file intake, themes, tests, and Windows CI.

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

## Convert an image

1. Add files through Workspace using Browse Files or drag-and-drop.
2. Open Images, select one file, and choose Inspect image.
3. PNG selects JPEG output automatically; JPEG selects PNG.
4. Choose a new output location, then select Convert image.
5. Wait for the saved-and-verified message. Original files and existing destination files are never overwritten.

PNG transparency is composited onto white (#FFFFFF) for JPEG. JPEG quality is fixed at 90 and PNG output uses 8 bits per component. JPEG → PNG does not restore details already lost to JPEG compression.

Inputs are limited to 25 MiB, 8192 pixels per side, and 16 million pixels. PNG above 8 bits, animation, multi-frame images, CMYK, embedded ICC profiles, and nonstandard PNG color configurations are rejected. EXIF orientation is applied to pixels; the EXIF profile is then removed to prevent repeated rotation. Unprofiled RGB/sRGB/grayscale and standard sRGB PNG color information are supported. This phase does not promise general color-managed or metadata-preserving conversion.

See [image-engine-decision.md](docs/image-engine-decision.md) for the pinned dependency and protection limits, and [phase-2b-validation.md](docs/phase-2b-validation.md) for verification evidence.

## Phase 2B screenshots

- [English — dark](docs/screenshots/12-phase-2b-english-dark.png)
- [English — light](docs/screenshots/13-phase-2b-english-light.png)
- [Arabic — light](docs/screenshots/14-phase-2b-arabic-light.png)
- [Arabic — dark](docs/screenshots/15-phase-2b-arabic-dark.png)

## Historical shell screenshots

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

Phase 2B supports one PNG or JPEG per operation. File intake can contain multiple files, but only the explicitly selected image is processed. WebP, BMP, HEIC/AVIF, batch conversion, advanced settings, other engines, Explorer integration, and installers remain unavailable. No new dependencies were added; Magick.NET-Q8-AnyCPU remains pinned to 15.0.0.
