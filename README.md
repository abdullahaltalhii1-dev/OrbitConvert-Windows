# OrbitConvert

OrbitConvert is a bilingual Windows desktop application for local file conversion. It converts single PNG, JPEG, WebP, and BMP images and supported videos to MP4 (H.264/AAC). It includes safe output handling, Arabic/English localization, runtime RTL/LTR, file intake, themes, tests, and Windows CI.

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
dotnet format OrbitConvert.slnx --verify-no-changes
```

For real video tests and local video development, acquire the pinned verified tools first:

```powershell
$env:ORBITCONVERT_FFMPEG_DIR = ./tools/Get-FFmpeg.ps1
$env:ORBITCONVERT_REQUIRE_FFMPEG = '1'
```

Without tools, video integration tests explicitly skip; CI requires them. No tools are installed system-wide. Runtime uses the bundled application-local `ffmpeg` folder first, or the absolute development override above; it never downloads anything or searches PATH. A future offline installer will bundle engines and the .NET runtime, but packaging and distribution-license approval remain deferred.

## Run

```powershell
dotnet run --project src/OrbitConvert.App/OrbitConvert.App.csproj
```

The startup window opens the Studio workspace. Browse Files uses the native Windows picker, and individual files can be dropped into the workspace. The selection remains in memory and can be edited or cleared. Settings can switch the entire interface between English and Arabic immediately; the language preference is stored at `%LOCALAPPDATA%\OrbitConvert\language.txt`. Theme and accent choices remain session-only.

## Structure

- `src/OrbitConvert.App` — WPF desktop executable and presentation code
- `tests/OrbitConvert.Tests` — xUnit tests
- `tools/ImageBenchmark.cs` — standalone measurement helper used for the Phase 2D performance figures (not part of the app or the test run)
- `docs/architecture.md` — architecture and phase-boundary notes
- `docs/image-engine-decision.md` — pinned Magick.NET package and Q8/Q16 assessment
- `.github/workflows/ci.yml` — Windows restore, Release build, and test workflow

## Convert an image

1. Add files through Workspace using Browse Files or drag-and-drop.
2. Open Images, select one file, and choose Inspect image.
3. Choose the output format: PNG, JPEG, WebP, or BMP. PNG suggests JPEG; other sources suggest PNG.
4. Optionally set quality (JPEG/WebP, 1–100, default 90) and resize. Aspect ratio is preserved by default, and the expected output size is shown.
5. Choose a new output location, then select Convert image.
6. Wait for the saved-and-verified message. Original files and existing destination files are never overwritten.

Transparency is preserved in PNG and WebP and composited onto white (#FFFFFF) for JPEG and BMP. Output uses 8 bits per component. Lossy sources do not regain detail when converted to lossless formats.

Inputs are limited to 25 MiB, 8192 pixels per side, and 16 million pixels; resize targets use the same limits. Sources above 8 bits, animation (including animated WebP), multi-frame images, CMYK, embedded ICC profiles, compressed/embedded BMP variants, and nonstandard PNG color configurations are rejected. EXIF orientation is applied to pixels; the EXIF profile is then removed to prevent repeated rotation. Unprofiled RGB/sRGB/grayscale and standard sRGB PNG color information are supported. This phase does not promise general color-managed or metadata-preserving conversion.

See [image-engine-decision.md](docs/image-engine-decision.md) for the pinned dependency and protection limits, and [phase-2b-validation.md](docs/phase-2b-validation.md), [phase-2c-validation.md](docs/phase-2c-validation.md), and [phase-2d-validation.md](docs/phase-2d-validation.md) for verification evidence, including measured performance and limits.

## Convert a video (Phase 3)

1. Add a local video in Workspace, then open Video and select one source.
2. Inspect video to see duration, resolution, container, codecs, size, and stream counts.
3. Choose High, Balanced (default), or Smaller-file quality. Optionally cap height at 1080p, 720p, or 480p; no upscaling.
4. Choose a new `.mp4` destination. Existing files and originals are never overwritten.
5. Convert video. Encoding and full output verification have separate real progress stages; an unknown duration stays indeterminate.
6. Cancel to terminate the owned process and clean its temporary output. Closing while busy offers to keep waiting or cancel and close after cleanup.

MP4/MOV, MKV/WebM, and AVI inputs are accepted only with explicitly supported codecs. Output is one H.264 stream and AAC audio when present; extra audio streams, subtitles, attachments, chapters, and metadata are not preserved. Quality changes compression, not a guaranteed target size. See [Phase 3 validation](docs/phase-3-video-validation.md) for exact formats, limits, licensing considerations, measurements, tests, and known limitations.

Actual application screenshots: [Arabic dark success](docs/screenshots/23-phase3-arabic-dark-success.png), [English light success](docs/screenshots/24-phase3-english-light-success.png), [real progress](docs/screenshots/25-phase3-real-progress.png).

## Phase 2D screenshots

- [Close blocked while a conversion runs](docs/screenshots/19-phase-2d-close-blocked-while-busy.jpg)
- [Arabic — missing-source error](docs/screenshots/20-phase-2d-arabic-missing-source.jpg)
- [Arabic — light, minimum window size](docs/screenshots/21-phase-2d-arabic-light-minimum-size.jpg)
- [English — dark, minimum window size](docs/screenshots/22-phase-2d-english-dark-minimum-size.jpg)

## Phase 2C screenshots

- [Arabic — dark, WebP with resize](docs/screenshots/16-phase-2c-arabic-resize.jpg)
- [Arabic — dark, WebP saved and verified](docs/screenshots/17-phase-2c-webp-success.jpg)
- [English — light, BMP saved and verified](docs/screenshots/18-phase-2c-english-light.jpg)

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

The app processes one image or one video at a time through its UI. Image operations cannot be cancelled once started (see phase-2d-validation.md); closing is blocked until they finish. Video operations support cancellation and bounded process timeouts. Intake can contain multiple files, but conversion is never automatic or batched. HEIC/AVIF, audio-only conversion, documents, color management, metadata preservation, Explorer integration, and installers remain unavailable. Magick.NET-Q8-AnyCPU stays pinned to 15.0.0; Phase 3 adds external FFmpeg/FFprobe executables, not a new NuGet dependency.
