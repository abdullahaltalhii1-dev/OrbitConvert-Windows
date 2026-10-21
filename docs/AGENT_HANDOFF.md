# Agent handoff log

Shared notes between coding assistants (Codex, Claude). **Read the newest entry first and verify it against live Git/GitHub before acting.** Add a new entry at the top when you hand off. Never rewrite earlier entries.

---

## 2026-10-21 — Codex: Phase 3 complete, awaiting owner review

### Live state and ownership

- Existing branch: `feature/phase-3-video-ffmpeg`; existing **Draft PR #10**: https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/pull/10. Do not merge or start Phase 4 without owner authorization.
- Claude's `4aed00d` backend and `9c7b732` handoff are preserved. Codex backend fix: `da77e8688fce44070fb1e5be301218ecbf635fbe`; completed UI/code checkpoint: **`929eaa9a32c30fd171266435f72f84dc86683f0c`**. This entry is delivered by a subsequent documentation/evidence commit; resolve current branch HEAD on GitHub before acting.
- Codex stopped after validation/delivery. No other assistant was delegated work. Resume only the owner's requested review/fix, not a new phase automatically.
- Pre-existing untracked `src/OrbitConvert.App/OrbitConvert.App_dcgdz3wr_wpftmp.csproj` was left untouched. No user changes were discarded. Local generated GUI videos are under ignored `artifacts/phase3-gui`; not uploaded or bundled.

### Changes since Claude's handoff

- Reproduced the original playlist failure locally. FFprobe says nonstandard m3u8 extension, `moov atom not found`, `Invalid data found when processing input`. Changed only the test expectation to InvalidOrCorruptVideo, keeping all security whitelists.
- Replaced the flaky global FFmpeg process count with a retained handle to the operation-owned encoder; cancellation asserts exit, absence of destination/temporary output, and confirmed cleanup.
- Propagated unconfirmed termination explicitly (ProcessTerminationFailed/CleanupFailed). Late starts remain best-effort terminated by the runner continuation. Pre-cancelled requests never start processes; last-moment cancellation is checked before finalization. Added duration-overflow and destination/cancellation finalization-race tests.
- Added VideoConversionViewModel, VideosView XAML and native SaveFileDialog boundary. Reuses intake, displays real metadata, MP4 settings/quality/max-height, real encoding/verification progress, cancel, localized status and cleanup warnings. No automatic/batch conversion.
- Added English/Arabic resources and live RTL/LTR behavior. Video navigation no longer says Coming Soon. Image/video panels disable conflicting operations. Closing video work offers cancel-and-wait or keep-waiting; images retain their previous wait-only behavior.
- Added 9 focused VM tests plus actual STA WPF layout/binding checks in both languages at minimum/default/large sizes. No new NuGet packages or architecture rewrite.
- README, architecture and `docs/phase-3-video-validation.md` now document setup, limits, exact formats/presets, binary checksum, licensing boundary, measurements and honest GUI results. Original roadmap/AGENTS rules unchanged.

### Verification actually completed

- Standard final `dotnet restore OrbitConvert.slnx`, Release build (`--no-restore`), tests (`--no-build --blame-hang-timeout 90s --blame-hang-dump-type none`), `dotnet format OrbitConvert.slnx --verify-no-changes`, and `git diff --check` passed. Build **0 warnings/errors**; **307 passed, 0 failed, 0 skipped**, 27 s locally. Earlier builds also passed using UseSharedCompilation=false and -m:1.
- Both UI checkpoint CI runs **passed** at 929eaa9: push **39532826362**, PR **39532829911**, 307 tests with actual pinned FFmpeg (push duration 32 s). Backend fix checkpoint also passed both workflows at da77e86, 293 tests. Final documentation commit triggers the unchanged gate again; inspect its checks before reviewing/merging.
- Actual GUI: Unicode/Arabic filename with spaces and &, explicit metadata inspection, native Save (no precreated output), MP4 success, Arabic dark/English light and live language/theme switching, navigation, source/quality combo boxes, real progress, cancellation, source/settings busy protection, default/maximized rendering and normal close.
- Actual cancel-and-close: generated 20-minute video, running encoder PID 14584, dialog captured at 19%; confirmation exited app PID 40224 and encoder, **no destination or temporary files**. Keep-waiting was separately verified. Shorter preliminary attempts finished before confirmation and are not cancellation proof.
- Manual image regression: PNG→JPEG in existing Images page saved/verified; original source hashes unchanged. Full previous image/intake/localization tests green. No binding errors captured by the WPF test.
- Actual screenshots: `docs/screenshots/23-phase3-arabic-dark-success.png`, `24-phase3-english-light-success.png`, `25-phase3-real-progress.png`, `26-phase3-cancel-to-close.png`.
- Measured both encoder quality orders with SSIM/time/bytes; exact one-run figures and methodology in validation document. Do not generalize them to real-world compression guarantees or hardware benchmarks.

### Known limitations / next authorized step

- Await technical review and owner approval. **No code work remains identified in the approved Phase 3 scope; no Phase 4 work was started.** Keep PR Draft/unmerged.
- Visual 125%/150% DPI and actual minimum-size resizing not completed. Automated min/default/large layout passed, real default/maximized observed. Border-drag attempt did not resize and is not a pass.
- No suspended-process hang reproduced in successful bounded runs; Norton remains an unconfirmed historical hypothesis. One build collided with the running test app's apphost lock and passed after normal close. No antivirus/security exclusions or setting changes.
- Final distribution licensing is still owner-controlled: development FFmpeg 9.0.2 Essentials is GPL-enabled. App-local/offline engine resolution exists, but no installer or approved redistributable bundle was implemented. See official FFmpeg licensing link and options in validation doc; do not silently choose a license strategy.
- Not a hostile-media sandbox or universal corrupt-input detector; time/metadata limits are not hard memory/CPU caps. Forced shutdown or unconfirmed late-start termination may leave owned temporary files; warnings document that risk.

---

## 2026-10-21 — Claude → Codex: Phase 3 WIP handoff (FFmpeg video engine)

The owner transferred active Phase 3 development to Codex. Claude has stopped and will not write to this branch.

### State

- **Branch:** `feature/phase-3-video-ffmpeg`, based on `main` at `b44a03b` (PR #9 merged).
- **Implementation checkpoint:** `4aed00d` (WIP). This handoff entry is committed on top of it.
- **Draft PR:** [#10 — Phase 3 (WIP): FFmpeg video conversion engine](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/pull/10), targeting `main`. **Do not merge.**
- **CI on `4aed00d`:** **Build passed. Tests: 290 passed, 1 failed, 0 skipped (291 total, 21 s)**, runs 39529040506 (push) and 39529068365 (pull_request). This includes all real FFmpeg end-to-end tests on a clean runner. The only failure is `VideoConversionTests.InvalidOrUnsupportedInput_IsRejectedWithoutCreatingFiles(kind: "playlist")`: expected `UnsupportedFormat`, actual `InvalidOrCorruptVideo`. The playlist is still **rejected safely**, with no output and no fetch; only the error classification differs.
- **Local worktree:** clean except one untracked file, `src/OrbitConvert.App/OrbitConvert.App_dcgdz3wr_wpftmp.csproj`. It was generated by the WPF build that stalled at 03:49, is not committed, and is safe to delete.

### Phase 3 scope (owner-approved)

- **Conversion:** real FFmpeg conversion to MP4 (H.264 + AAC) with practical quality and compression options.
- **Behavior:** real progress, cancellation, and timeouts, with safe process management and the same file-safety contract as images.
- **UI:** a bilingual Video workspace, in both themes.
- **Not in scope:** no Phase 4 audio, no batch work (Phase 8), no installer.
- **Mandatory product requirement:** the final product ships as one Windows installer. It must bundle the runtime and the legally redistributable engines, needs no internet for conversions, and has no PATH or environment setup. The code is already designed to load FFmpeg from an **app-local bundled folder**. The installer itself stays in the packaging phase.

### FFmpeg used for development and CI (owner-approved)

| Item | Value |
|---|---|
| Build | gyan.dev **9.0.2 essentials** |
| File | `ffmpeg-9.0.2-essentials_build.zip`, 114,768,076 bytes |
| Source | `https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/` |
| SHA-256 | `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba` |
| Configure flags | `--enable-gpl --enable-version3`, libx264, libx265, mediafoundation, and more |

- `tools/Get-FFmpeg.ps1` downloads it, verifies size and SHA-256, and extracts it to `%LOCALAPPDATA%\OrbitConvert\dev-tools\ffmpeg-9.0.2\ffmpeg-9.0.2-essentials_build\bin`, which is outside OneDrive. It never changes PATH.
- CI runs the same script. It sets `ORBITCONVERT_FFMPEG_DIR` and `ORBITCONVERT_REQUIRE_FFMPEG=1`, so real tests **fail instead of skipping** when the build is missing. The workflow also has a 45-minute job timeout and a 10-minute hang timeout for tests.
- Capabilities verified with the binaries:
  - Encoders: libx264, h264_mf, aac, plus hardware H.264 encoders.
  - Decoders for every allowed input codec.
  - With `-protocol_whitelist file` and `-format_whitelist`, an HLS playlist pointing at `http://` is rejected as "Format not on whitelist".

### Licensing and distribution: owner decision pending

This build is **GPLv3**. Shipping it requires GPL obligations (license texts, complete corresponding source for the exact build including x264/x265, no added restrictions), and H.264/AAC patent considerations must be reviewed. Options to present to the owner before packaging:

- **A) LGPL build plus Windows Media Foundation H.264 (`h264_mf`) and native `aac`.** The code already supports this: `FfmpegCapabilities.PreferredH264Encoder` falls back to `h264_mf` when libx264 is absent. Quality control is weaker, and the encoder may use the GPU.
- **B) Ship this GPL build as separate executables (aggregation),** with full GPL compliance.
- **C) Recommended for packaging: a custom minimal, reproducible FFmpeg build.** It contains only the needed demuxers, decoders, and encoders, with network protocols disabled. It is smaller, and the source offer is simpler.

### Architecture implemented (files and roles)

- **`src/OrbitConvert.App/Services/Media/ProcessRunner.cs`** — reusable process layer (for Phase 4 too):
  - Direct start with `ArgumentList` and `UseShellExecute=false`; no cmd or PowerShell. stdin is closed.
  - Bounded stdout/stderr capture (256K characters) with optional per-line callback.
  - `Timeout`, `InactivityTimeout` (no stdout), and cancellation; on stop it calls `Kill(entireProcessTree: true)`.
  - Kill-on-close **job object**, so children cannot outlive the app.
  - **`StartTimeout`** (default 60 s). `Process.Start` runs on a worker task; if it does not return, the run reports `TimedOut` and any late-starting process is killed. Unit-tested through an internal start seam.
  - **Latest edit, not compiled locally:** post-kill waits are bounded (10 s grace, then `TerminateJobObject`, then give up), and `ProcessRunResult.TerminationConfirmed` was added.
- **`Services/Media/FfmpegLocator.cs`**:
  - Resolution order: `<app>\ffmpeg\ffmpeg.exe` + `ffprobe.exe` (bundled, for the installer), then `ORBITCONVERT_FFMPEG_DIR` (development override, absolute paths only). PATH is never used.
  - `ProbeAsync` and `ParseCapabilities` read the version and the libx264, h264_mf, and aac encoders.
- **`Services/Video/VideoModels.cs`** — options, errors, inspection records, progress, and **video-specific limits**:
  - Size and length: 32 GiB, 12 h.
  - Dimensions: 8192 per side, 8192×4320 pixels.
  - Streams: 32.
  - Timeouts: probe 60 s, inactivity 120 s, maximum 24 h.
  - Allowed demuxers: `mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi`.
  - Allowed video codecs: h264, hevc, mpeg4, vp8, vp9, av1, mpeg2video, mjpeg.
  - Allowed audio codecs: aac, mp3, opus, vorbis, flac, pcm_s16le, ac3.
- **`Services/Video/VideoProbeParser.cs`** — parses ffprobe JSON:
  - Stream selection: the first non-cover-art video stream; the audio stream with the default disposition, otherwise the first audio stream.
  - Rotation is read from the display matrix.
  - Limit checks; unknown duration stays `null` (progress then stays indeterminate).
- **`Services/Video/FfmpegArguments.cs`** — builds argument lists:
  - `file:` URLs, protocol and format whitelists, explicit `-map`.
  - `scale=W:H` to an exact even size (downscale only).
  - Metadata and chapters stripped; autorotation is applied to the pixels.
  - Quality presets:

    | Preset | libx264 (medium) | h264_mf quality mode | AAC |
    |---|---|---|---|
    | High | CRF 18 | 85 | 192k |
    | Balanced | CRF 23 | 65 | 160k |
    | Small | CRF 28 | 45 | 128k |

  - Output: `-movflags +faststart -f mp4`, with `-y` applying only to the owned temporary file.
  - A full-decode check (`-xerror`).
- **`Services/Video/VideoConversionService.cs`** — the safety contract:
  - Source held read-only (`FileShare.Read`) for the whole operation.
  - Same-path, existing-destination, and `.mp4` extension checks.
  - Owned `.orbitconvert-<guid>.tmp` created with `CreateNew`.
  - Conversion with real progress from `-progress pipe:1`.
  - Validation: container; exactly one h264 stream at the exact expected size with rotation 0; AAC present exactly when the source had audio; no extra streams; duration within max(1 s, 5 %); full decode.
  - Then `File.Move(overwrite:false)`.
  - Cleans up only its own temporary file (retried); a concurrency gate allows one video operation at a time.
- **`src/OrbitConvert.App/AssemblyInfo.cs`** — `InternalsVisibleTo("OrbitConvert.Tests")`, so the pure internals can be unit-tested.
- **Tests:**
  - `tests/OrbitConvert.Tests/ProcessRunnerTests.cs` — 7 tests.
  - `VideoEngineUnitTests.cs` — 29 tests; no FFmpeg needed.
  - `VideoConversionTests.cs` — real end-to-end tests.
  - `VideoTestSupport.cs`:
    - `[FfmpegFact]` / `[FfmpegTheory]` skip with a reason unless the pinned build is found or `ORBITCONVERT_REQUIRE_FFMPEG=1` is set.
    - Deterministic lavfi fixtures, cached per test run under `%TEMP%\OrbitConvert.Tests`.
    - An SSIM helper.
    - Fixture generation now runs through `ProcessRunner`, so a stalled start fails instead of hanging. Previously it called `Process.Start` directly, which could hang forever.

### Test results actually observed (be precise)

- **Passed locally: 36/36** (7 `ProcessRunnerTests` + 29 `VideoEngineUnitTests`), at about 04:05. That was before the last `ProcessRunner` edit.
  - The stalled-start timeout test returned in under 5 s.
  - The test proving arguments are literal (no shell): `%PATH%` and `$(whoami)` stayed unexpanded and Arabic text survived.
  - Timeout, inactivity, and cancellation tests.
- **Real `VideoConversionTests`, partial:**
  - `Convert_SupportedInputs`: **8 of 10** cases passed at about 04:17 — vp8/webm, hevc/mkv, h264 without audio, vp9/webm, h264+aac/mp4, h264+aac/mov, mpeg2+ac3/mkv, av1+flac/mkv. The run then froze on the `mpeg4.avi` case.
  - **Not yet observed locally:** the mpeg4/avi and mjpeg/avi cases, plus capabilities, inspect, quality presets for both encoders, resolution, rotation, stream selection, progress, cancellation, invalid inputs, file safety, concurrency, and output validation.
- **Not rerun on this branch:** the existing 223 image, intake, and localization tests. They are untouched.
- **Compile status of `4aed00d`:** the last local build attempt (about 04:40) stalled, but **CI built it successfully**, final `ProcessRunner` edit included.
- **CI (authoritative):** the full suite of 291 tests ran on GitHub; **290 passed**. The one failure is the playlist classification noted above. The local freezes did not occur there.

### The stalling problem (precise record)

All times are local on 2026-10-21. Commands were run from the OneDrive repository folder.

1. **About 03:21:** `dotnet test … --filter FullyQualifiedName~VideoConversionTests`.
   - After 3 cases passed, the testhost stopped making progress for more than 10 minutes.
   - The only child, **`ffprobe` pid 45968** (probing an output after a successful conversion), had 1 thread, 0 CPU, 2 MB working set.
   - The same ffprobe command run directly took about 140 ms.
   - Killing the child did not resume the test.
2. **About 03:43:** a temporary trace (since removed) showed the hang **inside `Process.Start`**: "before Start" was logged and "started" never was, while the child (pid 28004) existed.
3. **About 03:49:** `dotnet build src/OrbitConvert.App/OrbitConvert.App.csproj -c Release` stalled.
   - `dotnet` and **`VBCSCompiler`** (started 03:43) sat at 0 CPU; `dotnet build-server shutdown` also hung.
   - **Workaround that worked once:** kill VBCSCompiler and build with **`-p:UseSharedCompilation=false`**; the build then took 1–2 s.
4. **04:06:** a real test run with the start-timeout fix. The testhost was idle for 10 minutes with no FFmpeg child processes, probably a fixture `Process.Start` in the test helper, which was then unprotected.
5. **04:17:** a run with fixtures routed through `ProcessRunner`. 8 cases passed, then a freeze:
   - The child **`ffprobe` pid 34580** (probing source `mpeg4.avi`): its only thread was `Wait/Suspended`, with **0 modules loaded**.
   - **testhost pid 36932: 24 of 25 threads `Wait/Suspended`.**
   - Killing the child did not help.
6. **04:32 and 04:40:** a format + build + test job produced no output, and a plain build stalled for more than 150 s.
   - **MSBuild worker pid 41664 (started 04:32:36): all 28 threads `Wait/Suspended`** — even with `UseSharedCompilation=false`.
7. Between the stalls, process creation worked normally: `whoami` took 62 ms and `ffprobe -version` 140 ms.
8. **Event log (Application, last 3 h):**
   - No Norton detections were logged.
   - SecurityCenter reports Norton 360 as ON.
   - 8 WER `LiveKernelEvent` entries (codes 0x193 and 0x17d) at 03:13, around the first `h264_mf` experiment — probably a GPU driver live dump; not proven related.
   - No security prompt was visible on screen.

### Hypotheses, with evidence

- **H1 — external process suspension by security or performance software (Norton 360, including its "Norton Tools" service `nllToolsSvc`). Medium confidence, NOT confirmed.**
  - For: whole processes with all threads `Suspended`, which only an external suspend explains; it hits unrelated developer processes (MSBuild node, testhost, VBCSCompiler); a child left suspended mid-`CreateProcess` matches a frozen parent; a NortonUI process showed high CPU.
  - Against: nothing in the event log, no prompt, and Norton's history and settings were not checked.
- **Evidence against a code deadlock:** the same commit ran all 291 tests in 21 s on a clean GitHub Windows runner with no freeze. This strengthens H1 and H3 (machine-specific) over a bug in OrbitConvert.
- **H2 — process-spawning pattern as the trigger** (many short-lived unsigned executables, rapid `Process.Start`). Medium confidence as a *trigger* for H1. The job objects only contain children; they cannot suspend the parent.
- **H3 — OneDrive synchronisation** of the repository and `bin`/`obj`. Low confidence for the freezes: OneDrive causes transient file locks (seen in Phase 2D), not suspended threads. It may still slow builds.
- **H4 — compiler server / MSBuild bug.** Low confidence: MSBuild froze even with shared compilation off, so it looks like a victim, not the cause.
- **H5 — Windows power throttling / efficiency mode.** Low confidence: it throttles but does not suspend desktop processes.
- **H6 — GPU / `h264_mf` driver issue.** Low confidence for the freezes. It is a separate reliability risk to note for option A.

### Recommended troubleshooting plan for Codex

1. **Use CI as the source of truth** for real tests: `gh pr checks 10`, `gh run view <id> --log`. Fix any failures there first.
2. **Locally, run small groups with a time limit:**
   - `dotnet build OrbitConvert.slnx -c Release -p:UseSharedCompilation=false`
   - `dotnet test … --no-build --filter FullyQualifiedName~VideoEngineUnitTests`, then `~ProcessRunnerTests`, then `~VideoConversionTests` — each with `--blame-hang-timeout 5m --blame-hang-dump-type mini`. The dump shows the suspension.
3. **When a run stops progressing, record the thread states:**
   `Get-Process testhost,dotnet,VBCSCompiler,ffprobe,ffmpeg | % { $_.Name; $_.Threads | Group-Object {"$($_.ThreadState)/$($_.WaitReason)"} | % { "  $($_.Name)=$($_.Count)" } }`
   `Wait/Suspended` on every thread confirms an external suspension.
4. **Ask the owner** to look at Norton 360's security history and any performance feature that suspends or puts background apps to sleep, and to decide. **Do not disable antivirus or add exclusions as a default step.**
5. **With owner approval,** try a clone outside OneDrive (for example `C:\dev\OrbitConvert-Windows`) to rule H3 in or out.
6. Do not reintroduce direct `Process.Start` in tests. Use `ProcessRunner`, or `VideoFixtures.Run`/`Execute`.

### Remaining Phase 3 work, by priority

1. **Make CI green on PR #10:** fix the single failing case (`kind: "playlist"`).
   - In `VideoConversionService.ProbeAsync`, FFprobe on the runner evidently did not print "not on whitelist" for a playlist named `.mp4` (possibly the mov/mp4 demuxer is tried first because of the extension).
   - Inspect `probe.StandardError` in CI, then decide whether to classify that message as `UnsupportedFormat` or to change the test expectation. Keep the safety property: the playlist is never opened and nothing is fetched.
2. **Service follow-ups:**
   - Use `ProcessRunResult.TerminationConfirmed`: report `CleanupFailed` or a clear error when termination was not confirmed.
   - Decide whether a long suspension of the app itself should be treated as inactivity.
3. **Video UI:**
   - `VideoConversionViewModel`: select from intake, inspect, show metadata (container, duration, size, codecs, streams), quality and maximum-height options, destination (SaveFileDialog `.mp4` with `CreateTestFile=false`), Convert, Cancel, a determinate progress bar (indeterminate when duration is unknown), elapsed time, localized statuses and errors including tools unavailable or unsupported.
   - `VideosView.xaml` replaces the video `ComingSoonView` in `MainWindow.xaml`.
   - Sidebar badge and automation names.
   - Close protection for a busy video operation: offer to cancel or wait.
   - New `LocalizationKeys.RequiredKeys` plus `Strings.resx` and `Strings.ar.resx`.
   - ViewModel tests with a fake `IVideoConversionService`.
4. **`docs/phase-3-video-validation.md`:** versions, capabilities, setup, the licensing assessment above, supported formats, options mapping, process and cancellation behavior, safety guarantees, real results, performance, limitations.
5. **Measurements and docs:** performance measurements (MP4/H.264/AAC: time, size, SSIM), GUI validation in both languages and themes, README and architecture updates.
6. **Final gate,** then update this handoff log.

### How to continue without repeating work

- Do not redo the FFmpeg selection or download. Run `tools/Get-FFmpeg.ps1`; it is idempotent and verified.
- The engine layer and its tests exist. Extend them rather than rewrite them.
- The argument and parser design is covered by unit tests, so change it only with matching test updates.
- Keep the app-local bundled `ffmpeg\` resolution, because the installer depends on it.

---

## 2026-10-21 — Claude: Phase 2D review follow-up (PR #9)

### State

- **Branch:** `feature/phase-2d-image-hardening`, on top of `4c6e2c8`.
- **PR:** [#9](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/pull/9), still Draft and not merged.

### What changed and why

An independent review flagged two edge cases in the optimized seekable path of `ImageInputGuard.ReadBounded`. Both were reproduced with regression tests before any fix:

1. **Position beyond Length.** `remaining` went negative and `new byte[remaining]` threw `OverflowException` out of the engine.
   - Fix: when `remaining <= 0`, return `EmptyInput`. This matches the behavior before the optimization.
2. **Stream ends before its declared Length.** A shortened snapshot was accepted as a valid image.
   - Fix: if the read stops before the declared length, reject it as `InvalidOrCorruptImage`. This is the same error already used for a stream that grows mid-read.

No other behavior changed. The byte limits are unchanged, and so is the single exact-size buffer.

### Verification actually run

| Check | Result |
|---|---|
| restore | Passed |
| Release build | 0 warnings, 0 errors |
| tests | **223 passed**, 0 failed, 0 skipped (221 before, plus 2 new tests) |
| format | Passed |
| `git diff --check` | Passed |

- **Memory re-check** (heaviest benchmark cases): 16 MP → BMP peaks at 281 MiB and 16 MP → PNG at 193 MiB. These are unchanged from the Phase 2D measurements.
- **GUI:** not re-run. The change only affects inconsistent seekable streams, which the GUI cannot produce.

### Next steps

- The owner reviews PR #9.
- **Only the owner merges.**
- Do not start Phase 3 without explicit approval.

---

## 2026-10-20 — Claude: Phase 2D image engine hardening

### State

- **Branch:** `feature/phase-2d-image-hardening`, based on `main` at `4b3a93f` (PR #8 merged).
- **PR:** [#9 — Phase 2D: image engine hardening and validation](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/pull/9), Draft, targeting `main`. Implementation commit: `58d746c`. Check the PR for the latest CI result.
- **Worktree:** clean after commit.
- **Not merged.** No Phase 3 work was started.

### What changed and why

1. **`MagickImageEngine`: BMP output is written uncompressed.**
   - Why: ImageMagick's BMP3 writer produced RLE8 for palette and grayscale images, and the input guard rejects RLE. Any image with 256 colors or fewer (including grayscale JPEG and PNG) failed with "The output image could not be verified".
   - Evidence: 5 regression cases failed before the fix and pass after.
2. **`ImagesView`: `SaveFileDialog.CreateTestFile = false`.**
   - Why: the dialog's 0-byte write-test file sometimes survived on the OneDrive folder (sync or antivirus held it). It then blocked the conversion as an existing destination and was left in the user's folder.
   - Evidence: reproduced in the real GUI and confirmed fixed there.
3. **`ImageInputGuard.ReadBounded`: seekable streams are read into one exact-size buffer.**
   - Why: the old growing `MemoryStream` plus `ToArray` held several transient copies of large outputs during validation.
   - Behavior: same limits; a stream that grows mid-read is rejected.
   - Evidence: peak working set for 16 MP → PNG went from 421 to 193 MiB, and for 16 MP → BMP from 478 to 282 MiB, with unchanged times.
4. **Phase label** changed to `PHASE 2D` / `المرحلة 2D`.
5. **Tests:** `MalformedInputHardeningTests` (a deterministic corruption corpus of about 3,600 inputs) and `ImageHardeningTests`:
   - WIC-encoded variant matrix
   - exact limit boundaries and a real 16 MP conversion
   - snapshot reads
   - Unicode and long paths
   - simulated disk full, read-only source, ACL-denied folder
   - concurrent conversions to one destination

   Two helpers were added to `ImageFixtures`.
6. **Documentation:**
   - Added `tools/ImageBenchmark.cs`, a standalone measurement helper that is not in the solution.
   - Added `docs/phase-2d-validation.md` and screenshots 19–22, cropped to the app window.
   - Updated README, `architecture.md`, and `image-engine-decision.md`.

### Verification actually run

Run on this branch, with the app closed:

| Check | Result |
|---|---|
| `dotnet restore OrbitConvert.slnx` | Passed |
| `dotnet build OrbitConvert.slnx -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test OrbitConvert.slnx -c Release --no-build` | 221 passed, 0 failed, 0 skipped |
| `dotnet format OrbitConvert.slnx --verify-no-changes` | Passed |
| `git diff --check` | Passed |

- **Benchmark:** 26 Release cases, one fresh process each, run before and after fix 3. The tables are in `docs/phase-2d-validation.md`.
- **Real GUI** (UI Automation plus keyboard and mouse, native dialogs; computer-use tools were unavailable). Unicode test folder with Arabic, `&`, brackets, and parentheses. Results:
  - intake
  - 16 MP inspection (about 0.5 s)
  - busy state: controls disabled, second Convert refused, Settings reachable, close blocked with a message
  - Save dialog no longer creates a file
  - invalid inputs: fake, empty, truncated, and deleted source, with English and Arabic messages
  - destination protection
  - Arabic Light and English Dark at the 900 × 620 minimum size
  - normal shutdown
- **Forced-kill experiment:** leaves a partial `.orbitconvert-*.tmp`. No final output is written and the source is unchanged. This is documented.
- **Cancellation probe:** only WebP encoding honors ImageMagick cancellation. Not implemented; deferred to process isolation.

### Not verified

- 125% and 150% DPI (no approval to change display settings)
- screen reader
- uncontrolled malicious corpus
- real disk-full volume
- custom filenames typed into the Save dialog
- long paths through native dialogs

### Environment notes for the next agent

- OneDrive intermittently locks freshly created files in this repo for a few seconds. Retry deletes, or use `%TEMP%` for test outputs.
- An invisible Norton overlay covers part of the sidebar and swallows synthetic mouse clicks. Use keyboard activation, and only when the app's main window is in the foreground.
- Never send keys while a native dialog is open. A stray keystroke started a rename in the Save dialog; it was cancelled and the files were intact.

### Next steps

- The owner reviews the Draft PR. Push any fixes to the same branch.
- **Only the owner merges.** Do not start Phase 3 (FFmpeg) or any later phase without explicit approval.
- Candidate follow-ups need owner approval: process-isolated conversion (enables real cancellation and crash containment), and DPI checks at 125% and 150%.

---

## 2026-10-20 — Claude: Phase 2C final validation (review only)

### State

- **Branch:** `feature/phase-2c-extended-images`, reviewed at `4ab0cf3`.
- **PR #8:** Draft, open, mergeable. CI `build-and-test` passed (2 runs).
- **Changes from this review:** documentation only. This entry plus `docs/phase-2c-validation.md`. No source or test changes.

### Local gate

Run on `4ab0cf3` with the app closed:

- restore: passed
- Release build: passed, 0 warnings, 0 errors
- tests: 114/114 passed
- format: passed

### Windows GUI validation

- **Method:** the real Release app was driven via UI Automation and real mouse clicks, with the native Open and Save dialogs.
- **Verification:** outputs were decoded independently with Magick and WIC.
- **Fixtures:** verified RGBA (color type 6) with exact alpha values, in `artifacts/phase-2c-review/` (gitignored).

Results:

- **Opaque RGBA PNG:** → WebP passed (alpha dropped). → PNG passed (pixel-identical).
- **Transparent PNG:** → WebP passed (alpha 0/128/255 kept). → JPEG and → BMP passed (white composite, correct blend).
- **Resize 200×200:** ratio kept gives 200×150, passed. Ratio off gives 200×200, passed.
- **JPEG q20 vs q95:** estimated quality 20/95; size 9 KB vs 165 KB; RMSE 0.127 vs 0.016. Passed.
- **WebP q20 vs q95:** 18 KB vs 82 KB. Passed. The RMSE gain is small because lossy WebP always uses 4:2:0 chroma.
- **Existing destination:** rejected. Content, hash, and source unchanged; no temp files left. Passed.
- **Localization and themes:** Arabic + Light passed (RTL, LTR paths). English + Dark and English + Light passed. Live language switch updates the status message.

### Native warning behavior

- Truncated JPEG: rejected. Passed.
- Unknown ancillary chunk: accepted. Passed.
- Empty tEXt keyword: accepted (OptionWarning). Passed.
- tEXt with a bad CRC: rejected via `MagickCoderWarningException`.
  - **Owner decision:** keep this strict handling unchanged. It is documented as a known limitation.

### Findings

- No defects found.

### Not verified

- 125%/150% DPI, screen reader, malicious corpus, memory use, disk-full behavior.
- Typing a custom filename in the Save dialog (automation limit; default names were used).

### Next

- The owner decides on merging PR #8.
- No Phase 2D without explicit approval.

---

## 2026-10-20 — Claude → next agent (Codex or Claude)

### State at handoff

- **Branch:** `feature/phase-2c-extended-images`, pushed and tracking `origin`.
- **Commit:** `aed2ab652fd22e8dedab1cb37a77be3b0eb71507` (Phase 2C), plus this handoff-docs commit on top.
- **PR:** [#8 — Phase 2C: WebP/BMP, quality, and resize](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/pull/8), **Draft, open, not merged**.
- **CI:** passed on `aed2ab6`.
- **Worktree:** clean. Nothing was left uncommitted.
- **`main`:** `662965f` (PR #7). PRs #1–#7 are merged.

### What happened in this session

1. **Found Codex's Phase 2C work uncommitted.** It covered 16 modified files plus `Models/ImageConversionOptions.cs`, `ExtendedImageConversionTests.cs`, and three screenshots. It was all kept and built upon; nothing was reset or rewritten.
2. **Ran the gate on Codex's work as-is.** Build passed, and 110/110 tests passed. `dotnet format --verify-no-changes` **failed**: the two new files used LF line endings, and some multi-arm `switch` expressions sat on one line.
3. **Review found a real bug.** A fully opaque RGBA PNG converted to PNG or WebP failed with `OutputValidationFailed`. The encoder drops the unused alpha channel, so the output's `HasAlpha` no longer matched the expected value.
   - **Fix:** `MagickImageEngine.Process` now runs `image.Alpha(AlphaAction.Off)` when `HasAlpha && IsOpaque`, before encoding.
   - **Test:** `OpaqueAlphaChannelSource_ConvertsToEveryTarget`, a Theory covering all 4 targets.
4. **Fixed formatting:** converted the new files to CRLF and ran `dotnet format whitespace` on the two affected files.
5. **Docs:**
   - Added `docs/phase-2c-validation.md`.
   - Updated `README.md`, `docs/architecture.md`, and `docs/image-engine-decision.md`.
6. **Final gate:** restore passed; Release build passed with 0 warnings and 0 errors; **114/114 tests passed**; format check passed.
7. **Shipped:** committed, pushed, opened Draft PR #8. The PR body source is `artifacts/phase-2c-pr.md`, which is gitignored and exists locally only.

### Not verified / open points

- The GUI was **not re-run after the opaque-alpha fix**. The screenshots (16–18) come from Codex's run before the fix. A quick manual check is worth doing: convert an RGBA screenshot PNG to WebP and to PNG.
- `MagickCoderWarningException` is now treated as corruption, alongside `MagickCorruptImageWarningException`. This is conservative and may reject some recoverable real-world files. The owner may want to review it.
- Same-format conversion (for example PNG → PNG) is now allowed, intended for use with resize.
- Not checked: 125%/150% DPI, a screen-reader pass, a malicious-image corpus, memory benchmarks, disk-full behavior, and crash recovery.
- **Environment notes:**
  - A running `OrbitConvert.App.exe` locks `bin\Release`. Close it before building.
  - `gh` is now authenticated as `abdullahaltalhii1-dev` on this machine.

### Next steps (owner decides)

1. The owner reviews PR #8, possibly with ChatGPT. If changes are requested, push fix commits to the **same branch**. Do not open a new PR.
2. **Only the owner merges.** Do **not** start Phase 2D or any later phase without explicit owner approval.
3. Optional and separate: commit the handoff pack (`CLAUDE.md`, `docs/ORBITCONVERT_STATUS.md`, `docs/ORBITCONVERT_PROJECT_HANDOFF.md`, roadmap v3.0) from the owner's Downloads zip in its own docs PR.

### Commands used

```powershell
dotnet restore OrbitConvert.slnx
dotnet build OrbitConvert.slnx -c Release --no-restore
dotnet test OrbitConvert.slnx -c Release --no-build
dotnet format OrbitConvert.slnx --verify-no-changes
```
