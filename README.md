# OrbitConvert

OrbitConvert is a bilingual Windows desktop application for local file conversion. It converts single PNG, JPEG, WebP, and BMP images, supported videos to MP4 (H.264/AAC), audio to MP3/WAV/FLAC, and verified document/Markdown directions. It includes PDF tools, bounded ZIP/TAR/7z archive operations, safe output handling, Arabic/English localization, runtime RTL/LTR, file intake, themes, tests, and Windows CI.

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

For real audio/video tests and local media development, acquire the pinned verified tools first:

```powershell
$env:ORBITCONVERT_FFMPEG_DIR = ./tools/Get-FFmpeg.ps1
$env:ORBITCONVERT_REQUIRE_FFMPEG = '1'
```

Without tools, media integration tests explicitly skip; CI requires them. No tools are installed system-wide. Runtime uses the bundled application-local `ffmpeg` folder first, or the absolute development override above; it never downloads anything or searches PATH. A future offline installer will bundle engines and the .NET runtime, but packaging and distribution-license approval remain deferred.

## Run

```powershell
dotnet run --project src/OrbitConvert.App/OrbitConvert.App.csproj
```

The startup window opens the Studio workspace. Browse Files uses the native Windows picker, and individual files can be dropped into the workspace. The selection remains in memory and can be edited or cleared. Settings can switch the entire interface between English and Arabic immediately; the language preference is stored at `%LOCALAPPDATA%\OrbitConvert\language.txt`. Theme and accent choices remain session-only.

## Batch queue (Phase 8)

Open **Batch queue**, choose a preset and an existing local destination folder, Browse Files (or reuse the Workspace intake), then **Add intake selection to queue**. Review the planned names before **Start queue**. Clear intake and add another group/preset to build a mixed queue. Selection alone starts nothing.

The FIFO queue runs one job at a time, with a default limit of 100 retained jobs. It reuses image PNG/JPEG/WebP/BMP, video MP4, audio MP3/WAV/FLAC and the existing document/Markdown matrix. Presets use the displayed fixed defaults; detailed single-file settings and PDF/archive tools remain in their individual panels. Outputs use `source-orbitconvert.ext`; existing files and originals are never silently overwritten.

Cancel a pending or active job, Cancel queue, Retry failed explicitly, or Clear completed/errors. Errors are per file and normal failures allow later jobs to continue. Cleanup/worker-termination uncertainty stops scheduling and blocks retries. Image cancellation waits for the bounded native call before cleanup; it is not instantaneous. Closing a busy queue asks whether to cancel and wait or keep running. Language/theme changes preserve queued jobs. See [Phase 8 validation](docs/phase-8-batch-validation.md) for the exact matrix, limits and actual verification status.

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

Actual application screenshots: [Arabic dark success](docs/screenshots/23-phase3-arabic-dark-success.png), [English light success](docs/screenshots/24-phase3-english-light-success.png), [real progress](docs/screenshots/25-phase3-real-progress.png), [cancel before closing](docs/screenshots/26-phase3-cancel-to-close.png).

## Convert or extract audio (Phase 4)

1. Add an audio file or supported video in Workspace, open Audio, select one source and inspect.
2. Review actual codec, duration, sample rate, channels, reported bitrate and chosen track. Default audio track wins; otherwise the first audio track is selected.
3. Choose MP3 (128/192/320 kbps), WAV (signed 16-bit PCM) or FLAC (compression 0–8). Choose preserve rate, 44.1 kHz or 48 kHz. Mono/stereo count is kept; surround is rejected, not silently downmixed.
4. Choose a new destination and Convert / extract audio. The original and existing files are never overwritten. Cancel stops processing and waits for cleanup; closing offers cancel-and-wait or keep-waiting.

Source tags, cover art and unrelated streams are removed. WAV can reduce higher source precision to 16 bits. FLAC can preserve PCM16/24 exactly without resampling; float PCM/precision above 24 bits is not accepted for FLAC here. Lossy sources never regain lost detail. Audio does not require an H.264 encoder. See [Phase 4 validation](docs/phase-4-audio-validation.md) for tested input formats, output constraints, evidence, performance and limitations. No installer or new NuGet dependency is added.

Actual application screenshots: [English dark MP3 success](docs/screenshots/27-phase4-english-dark-mp3.png), [Arabic light extraction success](docs/screenshots/28-phase4-arabic-light-extraction.png), [real audio progress](docs/screenshots/29-phase4-progress-cancel.png), [cancel before closing](docs/screenshots/30-phase4-cancel-to-close.png).

## Convert documents and Markdown (Phase 5)

Add one source through Workspace, open Documents, inspect it, choose one of the verified output formats, choose a new destination and Convert document. Processing is indeterminate with real inspection/conversion/verification stages, not invented percentages. Cancel and cancel-before-close wait for process cleanup. Existing files and originals are never overwritten; HTML is never opened automatically.

Supported directions: DOCX → ODT/Markdown/TXT; ODT → DOCX/TXT; RTF → DOCX/TXT; Markdown → DOCX/HTML/TXT; HTML → Markdown; UTF-8 TXT → Markdown. Plain-text intent uses .md/.txt/.html extensions plus strict UTF-8 validation; office types are inspected by content. No PDF, spreadsheets, presentations, macros, linked images or exact-layout guarantee. Embedded validated PNG/JPEG may be retained; TXT loses formatting/images.

Development/CI only: `$env:ORBITCONVERT_PANDOC_DIR = ./tools/Get-Pandoc.ps1`; set `ORBITCONVERT_REQUIRE_PANDOC=1` to require real tests. Runtime resolves bundled `pandoc/pandoc.exe` first and never installs/downloads tools or searches PATH. Version 3.12.1 is pinned; no LibreOffice or new NuGet package is required for this matrix. Future packaging/licensing approval remains deferred. See [Phase 5 validation](docs/phase-5-documents-validation.md) for safety boundaries, Arabic direction, tested outputs and limitations.

Actual application screenshots: [Arabic dark Markdown → DOCX](docs/screenshots/31-phase5-arabic-dark-markdown-docx.png), [Arabic light DOCX → ODT](docs/screenshots/32-phase5-arabic-light-docx-odt.png), [English light DOCX → Markdown](docs/screenshots/33-phase5-english-light-docx-markdown.png), [indeterminate processing](docs/screenshots/34-phase5-indeterminate-progress.png). Generated DOCX/ODT were also visually checked in Microsoft Word; manual busy-close/cancel timing and full DPI/accessibility checks remain explicitly unverified.

## PDF tools (Phase 6)

The **PDF Tools** workspace was merged through PR #14. It uses pinned qpdf 12.4.2, PDFiumCore/native PDFium 157.0.8100, and PdfPig 0.1.16. Development/CI: `$env:ORBITCONVERT_QPDF_DIR = ./tools/Get-Qpdf.ps1`, then launch the app from that same shell (`dotnet run --project src/OrbitConvert.App`). Alternatively bundle the complete qpdf distribution under `<app>/qpdf` with `bin/qpdf.exe` and upstream notices. Real PDF integration tests are mandatory and fail rather than skip when tools are absent. Runtime never downloads engines.

Add inputs in Workspace, open PDF Tools, choose an operation and source, then explicitly **Inspect PDF inputs**. Merge uses an ordered list with Add/Move earlier/Move later/Remove. Split supports extraction, individual pages and split-after boundaries; Reorder accepts a page sequence; rendering exports selected/all pages as PNG at 150/300 DPI; extraction writes selectable text to UTF-8 TXT (not OCR). Page fields use one-based ASCII numbers, e.g. `3,1-2`; blank means all except split-after boundaries. Choose a new file, or **name a NEW results folder** for multiple outputs; an existing folder/file is never reused or replaced. Start and Cancel are explicit. Closing during PDF work asks Yes (cancel, await cleanup, close) or No (keep running); uncertain cleanup keeps the app open.

All encrypted PDFs, active content, forms, attachments and signatures are rejected. Structural operations do not promise document-level bookmark/tag/metadata preservation. Ordinary horizontal Arabic paragraphs, diacritics, lists, mixed English/Arabic, numbers and a simple table have exact Unicode regression coverage. Text extraction uses PdfPig ActualText/glyph positions and the Windows 11 system ICU inverse-bidi map inside the existing isolated worker; no extra NuGet package or engine download. Complex columns, rotated Arabic, ambiguous base direction and faulty/missing PDF Unicode maps still need review; UTF-8 validation is not a guarantee of semantic reading order. See [Phase 6 validation](docs/phase-6-pdf-validation.md) for safety/resource limits, native isolation, licenses and historical evidence. Manual 125%/150% DPI is owner-deferred to Phase 9, not passed. No OCR, PDF-to-Word or Office-to-PDF is included.

## Archives (Phase 7 — owner review)

Open Archives and choose Extract, Create ZIP, Create TAR, or Repackage to ZIP. Selection alone never processes content. For extraction/repackaging, select one archive and explicitly Inspect contents before naming a **NEW** destination. Creation has its own explicit file list and optional native folder picker; global intake still rejects folders. Empty archives/folders are supported. Start and Cancel are explicit; closing while busy offers No (keep running) or Yes (cancel and await cleanup). Progress reports real stages, not invented percentages.

Pinned **SharpCompress 0.50.7** reads bounded ZIP (Store/Deflate), uncompressed TAR (V7/USTAR/GNU long names), and 7z. ZIP/TAR creation and ZIP/TAR/7z → ZIP are tested; 7z/RAR creation, encrypted archives, ZIP64, compressed TAR, links, nested automatic extraction and unsupported profiles are refused. New files/directories are validated before no-overwrite publication. Originals remain read-only. Initial practical limits: 64 MiB input/output archive and single entry, 256 MiB total expanded content, 1,000 paths including implicit folders, depth 16 and 200:1 expansion (1 MiB floor). A contained child has a 512 MiB committed-memory quota and two-minute deadline. A pre-parser guard mitigates the unpatched 7z next-header allocation advisory; do not remove it merely because NuGet audit is green.

See [Phase 7 validation](docs/phase-7-archives-validation.md) for exact format profiles, security/cleanup boundaries, advisories, tests and actual Windows evidence. Genuine screenshots: [English dark ZIP creation](docs/screenshots/35-phase7-zip-created-english-dark.jpg), [Arabic dark extraction](docs/screenshots/36-phase7-extracted-arabic-dark.jpg), [Arabic light populated contents](docs/screenshots/37-phase7-arabic-light-contents.jpg). No Phase 8 queue, shell integration or installer is included.

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

The app runs one operation at a time through its UI. PDF merge and archive creation accept explicit lists; this is not a batch queue. Image operations cannot be cancelled once started (see phase-2d-validation.md); closing is blocked until they finish. Media/document/PDF/archive operations support cancellation and bounded process timeouts. Intake can contain multiple files, but processing is never automatic. HEIC/AVIF, general color management/metadata preservation, Explorer integration, and installers remain unavailable. Magick.NET-Q8-AnyCPU stays pinned to 15.0.0; FFmpeg/FFprobe, Pandoc and qpdf are external application-local tools. Phase 7 awaits owner review; no later phase is implemented.
