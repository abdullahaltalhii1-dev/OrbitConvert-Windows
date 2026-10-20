# Phase 2D validation — image engine hardening

Phase 2D hardens the existing single-image engine (PNG, JPEG, WebP, BMP). It adds no formats, engines, batch processing, or UI features. Magick.NET-Q8-AnyCPU stays pinned at 15.0.0. The strict decoder-warning policy, including `MagickCoderWarningException`, is unchanged as the owner approved.

## Defects found and fixed

### 1. Palette and grayscale images could not be converted to BMP

**Symptom.** The output always failed verification with "The output image could not be verified."

**Affected sources.** Any image with 256 colors or fewer: grayscale JPEG and PNG, palette PNG, and 8-bit BMP.

**Cause.** ImageMagick's BMP3 encoder writes palette images as RLE8. The input guard deliberately rejects RLE BMP, so output validation refused the app's own output.

**Fix.** `MagickImageEngine` sets `CompressionMethod.NoCompression` for BMP output. The app never writes a BMP variant it refuses to read.

**Evidence.** Before the fix, 5 cases failed: gray JPEG, gray PNG, indexed PNG, indexed BMP, and a hand-built palette BMP to BMP. After the fix, all pass, and the tests assert `BI_RGB` (compression 0).

### 2. The Save dialog's write-test file could block the conversion and remain in the user's folder

**Symptom.**

- Pressing **Save** created a 0-byte file at the chosen destination.
- On the OneDrive test folder, with sync and antivirus active, the dialog sometimes failed to delete that file.
- The conversion then reported "A file or folder already exists", and the 0-byte file stayed behind.

**Cause.** `Microsoft.Win32.SaveFileDialog.CreateTestFile` defaults to `true`.

**Fix.** `ImagesView` sets `CreateTestFile = false`. The conversion service already checks write access by creating its own exclusive temporary file in the destination folder.

**Evidence.**

- Before the fix, a 0-byte file was observed at the destination for 2 s after Save, and the conversion was rejected.
- After the fix, the destination never appeared during a 2-second window sampled every 50 ms, and the conversion succeeded. This was checked in the real GUI.

### 3. Validation of large outputs held several transient copies in memory

**Cause.** `ImageInputGuard.ReadBounded` copied seekable streams into a growing `MemoryStream`, then copied again with `ToArray()`. For a 48 MB BMP output, that meant roughly 112 MB of transient managed buffers during output validation.

**Fix.** When the stream length is known, the guard now reads directly into one exact-size buffer. The read is still bounded by the same limit. A stream that grows after its length was read is rejected as `InvalidOrCorruptImage`. The non-seekable path is unchanged.

**Evidence.** Peak working set, measured in the same Release benchmark before and after:

| Case | Before | After |
|---|---|---|
| 16 MP → PNG | 421 MiB | 193 MiB |
| 16 MP → BMP | 478 MiB | 282 MiB |
| 1080p JPEG → BMP | 101 MiB | 53 MiB |

Conversion times did not change.

### Other change

The sidebar phase label now reads `PHASE 2D` / `المرحلة 2D`.

## New regression coverage

The suite now has **221 tests** (Phase 2C had 114). There are two new test classes.

### `MalformedInputHardeningTests`

A deterministic, bounded corruption corpus.

**Valid seeds (11):** PNG RGBA, PNG RGB, baseline JPEG, progressive JPEG, lossy WebP, lossy WebP with alpha, lossless WebP, 24-bit BMP, 32-bit bitfield BMP, 8-bit palette BMP, and top-down BMP.

**Mutations (about 330 per seed, about 3,600 inputs in total):**

- Truncation at every one of the first 64 bytes and at 24 points across the file.
- Inversion of every byte in the first 160 bytes and of 48 payload bytes.
- `0xFFFFFFFF` written into every 4-byte header field.
- Trailing garbage and a concatenated copy.

**Invariant:** no exception escapes the engine, no input takes more than 5 s, and any accepted input converts to every target with outputs that pass validation.

### `ImageHardeningTests`

- **WIC-encoded variants to all 4 targets (72 cases).** Real files from the Windows encoders in less common pixel formats:
  - PNG: 1/2/4/8-bit gray and indexed, Bgr24, Bgra32.
  - BMP: 1/4/8-bit indexed, 24-bit, 32-bit, Bgra32.
  - JPEG: grayscale and color.
- **Exact limit boundaries:**
  - 16,000,000 pixels (4000 × 4000, 8000 × 2000) is accepted; 4000 × 4001 and 8192 × 1954 are rejected from the header, before pixel allocation.
  - Exactly 25 MiB passes the size check and one more byte is rejected, for both seekable and forward-only streams.
  - The 8193-pixel JPEG side limit is enforced from the frame header.
- **A real conversion at the 16 MP limit**, for every target:
  - decoding a 4000 × 4000 image;
  - upscaling a small image to 4000 × 4000.

  All outputs are validated and stay within the 96 MiB output bound.
- **Snapshot behavior:** reads that return only a few bytes at a time are assembled correctly; a stream that grows after its length was read is rejected.
- **Unicode paths:** Arabic, mixed script, emoji, punctuation, and nested folders, with a total path over 260 characters. The path is returned exactly and the source stays unchanged.
- **Simulated disk full during write:** `OutputFailure`, with no output and no temporary file left behind.
- **Read-only source:** converts, and its attribute and bytes are unchanged.
- **Destination folder with write denied (ACL):** clean `OutputFailure`, and nothing is created.
- **Four simultaneous conversions to the same destination:** exactly one succeeds and three get `DestinationExists`. The surviving file is valid and no temporary files remain.

All Phase 2B and 2C tests still pass, including the 16 format pairs, transparency, EXIF orientation, quality, resize, corrupt-JPEG warnings, races, and cleanup.

WebP outputs are checked by the engine's own full decode and by the RIFF structure. WIC decoding of WebP depends on the optional Windows WebP codec and is not used as an independent oracle in CI.

## Resource limits, confirmed

| Stage | Enforcement |
|---|---|
| Input read | `ReadBounded`: maximum 25 MiB (exact boundary tested), in one buffer |
| Container inspection | `ImageInputGuard`: maximum 8192 per side and 16,000,000 pixels, from headers, before pixel allocation (PNG, JPEG, WebP, BMP) |
| Decoding | Native policy: width and height 8192, pixel cache memory 256 MiB, no disk or map cache, list length 8, time 30 s; `MaxMemoryRequest` 128 MiB, `MaxProfileSize` 1 MiB. Ping runs before the full read. |
| Resizing | `ImageConversionOptions.IsValid` and `CheckDimensions` on the computed output size, before `Resize` |
| Encoding | Same native policy. Largest real outputs at 16 MP: BMP 48.0 MB, PNG 26.5 MB (noisy photo-like content) |
| Output validation | Re-read with a 96 MiB bound, then the same guard and a full native decode |

Native time and resource settings are **not** an OS-level sandbox or a guaranteed hard deadline. The process total also includes managed buffers and the .NET runtime, about 26 MiB at startup.

## Performance measurements

### Method

`tools/ImageBenchmark.cs` is a .NET file-based program that references the app project. It drives the real `ImageConversionService` and `MagickImageEngine`.

- Each case runs in a **fresh process**.
- Run 0 is **cold**: it includes native ImageMagick initialization and policy loading.
- Runs 1–5 are **warm** (runs 1–3 for 16 MP sources). The median and range are reported.
- Peak memory is `Process.PeakWorkingSet64` for the whole process after all runs.
- Inputs are synthetic and deterministic: smooth photo-like content with noise, a flat screenshot-like RGBA image, and a radial transparency image.
- Outputs were written outside OneDrive, so sync activity cannot lock files or skew I/O.
- Release build.

To reproduce:

```powershell
dotnet run -c Release --file tools/ImageBenchmark.cs -- gen <dir>
dotnet run -c Release --file tools/ImageBenchmark.cs -- run <source> <format> <outDir> <warmRuns> [width height]
```

**Machine:** Intel Core i7-12700F (12 cores, 20 threads), 47.8 GiB RAM, Kingston SNVS1000G NVMe, Windows 11 Pro 10.0.26200, .NET SDK 10.0.402, Magick.NET-Q8-AnyCPU 15.0.0.

### Results (after the fixes)

| Source | Size | Target | Output | Cold ms | Warm median ms | Peak MiB |
|---|---|---|---|---|---|---|
| photo 1920×1080 JPEG | 616 KB | PNG | 3.4 MB | 568 | 476 | 52 |
| | | JPEG | 633 KB | 196 | 116 | 54 |
| | | WebP | 399 KB | 339 | 253 | 63 |
| | | BMP | 6.2 MB | 148 | 77 | 53 |
| photo 1920×1080 WebP | 119 KB | PNG | 2.5 MB | 1192 | 1092 | 54 |
| | | JPEG | 347 KB | 180 | 97 | 54 |
| | | WebP | 160 KB | 289 | 198 | 57 |
| | | BMP | 6.2 MB | 142 | 61 | 54 |
| photo 1920×1080 BMP | 6.2 MB | PNG | 3.8 MB | 415 | 312 | 78 |
| | | JPEG | 631 KB | 186 | 105 | 76 |
| | | WebP | 452 KB | 338 | 250 | 85 |
| | | BMP | 6.2 MB | 144 | 57 | 75 |
| screenshot 1920×1080 RGBA PNG | 8 KB | PNG | 16 KB | 160 | 76 | 51 |
| | | JPEG | 492 KB | 151 | 73 | 56 |
| | | WebP | 190 KB | 251 | 168 | 59 |
| | | BMP | 1.0 MB | 135 | 53 | 46 |
| transparent 1024×1024 PNG | 103 KB | PNG | 105 KB | 205 | 117 | 45 |
| | | JPEG | 52 KB | 148 | 64 | 45 |
| | | WebP | 71 KB | 260 | 171 | 73 |
| | | BMP | 3.1 MB | 135 | 54 | 43 |
| photo 4000×4000 JPEG (16 MP) | 4.7 MB | PNG | 26.5 MB | 3765 | 3682 | 193 |
| | | JPEG | 4.9 MB | 982 | 884 | 194 |
| | | WebP | 3.1 MB | 2030 | 1930 | 247 |
| | | BMP | 48.0 MB | 622 | 570 | 282 |
| 16 MP JPEG, resized to fit 1920×1920 | 4.7 MB | JPEG | 709 KB | 940 | 846 | 142 |
| 1080p JPEG, upscaled to 4000×4000 | 616 KB | PNG | 9.9 MB | 7574 | 7417 | 98 |

### Observations

- Native initialization adds about 70–90 ms to the first conversion in a process.
- Every conversion re-reads and fully decodes its output for validation, so this cost is included in all timings.
- PNG encoding dominates the slowest cases. The worst case measured is a 4000 × 4000 upscale to PNG at about 7.5 s.
- These are single-machine measurements on synthetic inputs. They are not a general performance claim.

The raw TSVs are kept locally under the gitignored `artifacts/phase-2d-validation/bench/` folder (debug baseline, Release before, Release after).

## Failure recovery and file safety

| Scenario | Result | Evidence |
|---|---|---|
| Original never modified | Pass | Byte comparisons in all service tests and in the GUI runs. Read-only source test. |
| Existing destination never overwritten | Pass | Unit tests; GUI second conversion rejected with the existing file's hash unchanged |
| Destination appearing during conversion | Pass | Existing race test, plus four simultaneous conversions with exactly one winner |
| Failed conversion leaves no completed but invalid output | Pass | Corrupt, partial, and warning outputs fail validation and are never moved into place |
| Only owned temporary files cleaned | Pass | Existing cleanup tests. No directory sweeping was added. |
| Validation before finalization | Pass | `File.Move(overwrite: false)` runs only after a full re-decode |
| Missing source | Pass | Service tests; GUI shows "The source file could not be read…" in English and Arabic |
| Read-only or inaccessible destination | Pass | ACL deny test returns `OutputFailure` and leaves nothing behind |
| Invalid destination path or folder | Pass | Missing folder and wrong extension are covered by tests |
| Disk full | Simulated pass | An IOException with ERROR_DISK_FULL thrown mid-write gives `OutputFailure` and no files. A real full volume was not created. |
| Simultaneous requests | Pass | One-operation semaphore; the concurrency test above |
| Closing during an operation | Pass (GUI) | The close is cancelled and a "Please wait…" message is shown. The conversion completes and verifies. |
| Forced process termination | Known limitation | A 4000 × 4000 PNG conversion process was killed after 3.5 s (see below) |

**Forced termination in detail.** The killed process left one partial `.orbitconvert-<guid>.tmp` (3.8 MB) in the destination folder. No final output was created and the source was unchanged. The app intentionally never sweeps folders, so such a file must be removed manually.

## Cancellation feasibility (not implemented, deferred)

ImageMagick's `Progress` event was tested as a cooperative cancel signal, with `e.Cancel = true` set after 150 ms:

| Stage | Behavior |
|---|---|
| JPEG decode | No progress events, so it cannot be cancelled |
| Resize (upscale) | About 4,000 events, cancel ignored, runs to completion |
| PNG encode | Events raised, cancel ignored |
| WebP encode | Honored: aborted with "user abort" |

The slowest operations (resize plus PNG encode) are exactly the ones that ignore cancellation. A Cancel button would therefore be unreliable, and terminating a native decoder thread is unsafe.

Robust cancellation needs process isolation: run the conversion in a worker process that can be terminated, then clean up only that job's temporary file. That is an architectural change, outside Phase 2D. It is deferred, for example to the job and queue work in Phase 8, subject to owner approval.

Current mitigations:

- Inputs are capped at 16 MP; the worst measured case is about 7.5 s.
- The UI stays responsive during conversion.
- Closing the window is blocked until the operation finishes.

## Responsiveness and busy state

Measured in the GUI during a ≈7.5 s 4000 × 4000 PNG conversion:

- **Busy controls:** Inspect, Choose output location, Convert, and both combo boxes are disabled. A second Convert invoke is refused.
- **Responsiveness:** the UI tree answers UI Automation queries in about 110–200 ms. Navigating to Settings and back works mid-conversion.
- **Closing:** the window close is cancelled with the localized "Please wait…" message.
- **Completion:** the controls re-enable; the status shows "saved and verified"; no temporary files remain.

## Windows GUI validation

The real Release build of this branch was driven through Windows UI Automation and keyboard and mouse input, using the native Open and Save dialogs. Computer-use tools were not available in this session.

### Test folder

- Location: `artifacts/phase-2d-validation/gui/مجلد الاختبار - Test Folder (2D)/صور العميل & co. [نهائي]/`
- Characters in the path: Arabic, spaces, `&`, parentheses, brackets, and a dot.

### Scenarios

| Scenario | Result |
|---|---|
| Intake via the native Open dialog (8 files, Arabic and mixed names) | Pass |
| Large image inspection (16 MP JPEG) | Dimensions shown about 0.5 s after Inspect |
| Busy state, duplicate command, navigation during work, close protection | Pass (see above) |
| Save dialog creates no destination file (fix 2) | Pass |
| Invalid inputs | Pass: text file named `.png` gives "unreadable or damaged"; 0-byte file gives "This file is empty."; truncated JPEG gives "damaged"; deleted source gives "could not be read" (English and Arabic, live switch) |
| Destination protection | Pass: first WebP conversion saved; second to the same name rejected; existing file hash unchanged |
| English and Arabic, Dark and Light | Pass: Arabic RTL with LTR paths and numbers; language switch updates on-screen messages |
| Window sizes | Maximized and default checked. A request for 600 × 400 is clamped to the 900 × 620 minimum; the layout stays usable with a scrollbar (Arabic Light, English Dark). |
| Normal shutdown while idle | Process exited in 251 ms; no temporary files; language preference restored to `en` |

### Screenshots

Cropped to the application window:

- [Close blocked while busy](screenshots/19-phase-2d-close-blocked-while-busy.jpg)
- [Arabic missing-source message](screenshots/20-phase-2d-arabic-missing-source.jpg)
- [Arabic Light at minimum size](screenshots/21-phase-2d-arabic-light-minimum-size.jpg)
- [English Dark at minimum size](screenshots/22-phase-2d-english-dark-minimum-size.jpg)

### Environment notes

- Files in the OneDrive test folder were intermittently locked for a few seconds after creation (rename and delete denied). This affects test housekeeping only, not app correctness. It is the same condition that exposed defect 2.
- An invisible third-party antivirus overlay window covered part of the sidebar and intercepted mouse clicks. Navigation was done with keyboard activation (focus + Space) instead.

## Not verified

- **125% and 150% DPI:** changing global display scaling was not approved for this session.
- **Screen reader:** a full pass was not done. Automation names were exercised indirectly through UI Automation.
- **Malicious-image corpus:** an uncontrolled corpus was deliberately not used. The deterministic mutation corpus above is the substitute.
- **Real disk-full volume:** simulated only.
- **Forced termination:** only the single kill experiment above.
- **Custom filenames in the Save dialog:** not exercised by the GUI automation; default names were used.
- **Long paths through the native dialogs:** not exercised. Long paths are covered at service level.

## Remaining risks and deferred work

- In-process native decoding is not sandboxed. A native crash or hang would affect the app process.
- There is no user-facing cancellation (see above).
- Forced termination can leave an owned `.orbitconvert-*.tmp`. Detecting or cleaning such files safely would need a recorded job journal, which is not part of Phase 2D.
- The strict coder-warning policy can reject PNGs that are recoverable but have bad metadata CRCs (owner-approved, documented in Phase 2C).
- There is no color management. ICC, CMYK, and sources above 8 bits remain rejected.
