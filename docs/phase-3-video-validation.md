# Phase 3 — video conversion validation

## Final review follow-up — Windows job assignment

The reviewed base was `f7aac1ed6ed9490ca3c9ae0f4e1195d5ca40851d`. An unchecked FALSE return from AssignProcessToJobObject previously let a live uncontained child continue until ordinary exit, cancellation, or timeout. [Microsoft's API contract](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject) requires checking that return and retrieving the native error on failure.

- Deterministic regression uses the actual Windows API with an invalid job handle and a real running FFmpeg child, not a mocked managed exception. It observed FALSE / **ERROR_INVALID_HANDLE (6)** while the child was alive. Before the correction (only an internal native-call test seam added), the test failed: expected StartFailed, actual TimedOut. The already-exited race test passed.
- Assignment now checks the native result, captures the Win32 error before further OS calls, and fails explicitly if the child has not exited. The runner immediately uses the existing process-tree kill/bounded-wait path, reports StartFailed and retains the TerminationConfirmed result. It never claims a successful managed run for that uncontained live child.
- Job creation/configuration failure also fails closed **before** starting any child. No silent no-job fallback. A short-lived tool that has already exited is allowed to finish normally even when assignment fails. This is not an atomic start-and-assign implementation; kill-on-close coverage begins once assignment succeeds. Existing startup-timeout/late-start limitations remain unchanged.
- A real conversion regression injects the native assignment failure only for the encoder: the conversion reports the existing ToolsUnavailable startup error, preserves source hashes, creates no completed destination, removes its operation-owned temporary output and confirms cleanup.
- Focused run: **11 passed, 0 failed/skipped**, including process startup, normal cancellation, runtime/inactivity timeouts, live assignment failure, already-exited assignment failure and conversion cleanup. Build: zero warnings/errors.
- Full local gate: restore and standard Release build succeeded (zero warnings/errors), **310 passed, 0 failed, 0 skipped**, 28 seconds, with `ORBITCONVERT_REQUIRE_FFMPEG=1`. `dotnet format OrbitConvert.slnx --verify-no-changes` and `git diff --check` passed. The three new regressions extend the previously passing 307 tests; no previous test was removed.
- No packages, settings, UI, architecture, or future-phase features were added. Manual GUI was not rerun for this backend-only correction; prior GUI evidence below is unchanged.

## Backend checkpoint — 2026-10-21

Continues Claude's implementation on `feature/phase-3-video-ffmpeg`, Draft PR #10. No Phase 4 work.

- Reproduced the old playlist assertion locally: 8 passed, 1 failed in the focused nine-case run.
- The pinned FFprobe rejects the disguised `.mp4` playlist with `moov atom not found` and `Invalid data found when processing input`, not `not on whitelist`. The test now expects `InvalidOrCorruptVideo`. Production file/protocol/demuxer restrictions are unchanged.
- Replaced the cancellation test's system-wide process count with the actual encoder's retained process handle. It must exit before the operation returns; destination and owned temporary files must be absent, and cleanup must be confirmed.
- Unconfirmed process termination now surfaces explicitly rather than looking like clean cancellation or timeout. Late process creation is marked unconfirmed; its continuation still attempts termination. No destination is finalized in that state. Owned temporary cleanup remains best effort and is reported honestly.
- Restore and Release build succeeded locally (0 warnings/errors), using `-p:UseSharedCompilation=false -m:1` for the build.
- Full local suite: **293 passed, 0 failed, 0 skipped**, 26 seconds, including actual FFmpeg integration tests. `--blame-hang-timeout 90s --blame-hang-dump-type none` was enabled. No local freeze occurred in this run. This does not establish the cause of earlier stalls.
- UI completion, GUI checks, performance observations, and final CI evidence are recorded below.

## Tools and distribution boundary

Development/CI build: gyan.dev FFmpeg **9.0.2 Essentials**, archive `ffmpeg-9.0.2-essentials_build.zip`, 114,768,076 bytes, SHA-256 `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`. `tools/Get-FFmpeg.ps1` verifies the download before extraction. Application runtime never downloads anything or searches PATH.

Resolution order: `<application>\ffmpeg\ffmpeg.exe` plus `ffprobe.exe`, then absolute `ORBITCONVERT_FFMPEG_DIR` (development/CI). Future offline packaging must bundle the engines and runtime; no installer is implemented here.

The selected development build enables GPL/version3 and includes libx264/libx265. Final distribution licensing is **not approved by this checkpoint**. Owner review must cover exact binary/source correspondence, redistribution notices/source obligations and codec patent considerations. An LGPL-compatible Media Foundation build or a minimal reproducible FFmpeg build are possible alternatives, not decisions made here. [FFmpeg's official licensing guidance](https://ffmpeg.org/legal.html) distinguishes LGPL and GPL-enabled builds, calls out libx264, source correspondence, and jurisdiction-dependent patent questions. Consult that guidance and appropriate licensing advice before shipping; invoking separate executables is not by itself a compliance determination.

## Implemented engine and UI

- Inputs: MP4/MOV (mov/mp4/m4a/3gp/3g2/mj2 demuxer family), MKV/WebM, AVI.
- Selected video codecs: h264, hevc, mpeg4, vp8, vp9, av1, mpeg2video, mjpeg. Selected audio: aac, mp3, opus, vorbis, flac, pcm_s16le, ac3. Container acceptance alone does not imply every codec is supported.
- First non-cover-art video; default audio stream or otherwise first audio. Extra streams, subtitles, chapters, attachments, and metadata are not copied. Rotation is applied to pixels; output dimensions are even for 4:2:0. No upscaling.
- Output is MP4, one H.264 video and AAC when source audio exists. libx264 is preferred; h264_mf is a capability-based fallback, not a promise of hardware acceleration.
- High/Balanced/Small map to libx264 medium CRF 18/23/28, or Media Foundation quality 85/65/45; AAC 192k/160k/128k. Height options: source, 1080, 720, 480. A smaller file is not guaranteed.
- Video selection reuses intake; inspection is explicit. Metadata, quality/height, native destination selection, start, actual two-stage progress, cancellation, completion and errors are bilingual. Unknown duration remains indeterminate. Selecting/navigating never starts conversion. No batch or audio-only workflow.
- Async commands suppress duplicate submissions, freeze source/settings during work and wait for actual service completion. Cross-engine UI busy guards prevent simultaneous image/video operations. Closing offers keep-waiting or cancel-and-close after the service finishes cleanup.

## Safety and limits

Input limit: 32 GiB, 12 hours, 8192 per side, 8192 × 4320 pixels, 32 streams. Probe: 60 s. No stdout activity during encoding/verification: 120 s. Maximum stage runtime: 24 h. Default process-creation deadline: 60 s; bounded termination waits: 10 s plus 10 s after job termination. Already-cancelled requests never start a process. Huge reported durations are rejected before TimeSpan conversion can overflow.

The source is held read-only; arguments bypass shells, use `file:` paths and file-only protocols plus demuxer whitelists. No playlists or network URL processing. One unique `.orbitconvert-<guid>.tmp` is reserved with CreateNew in the destination directory. Output must pass container/stream/codec/size/orientation/audio/duration checks and a complete decode before `File.Move(overwrite:false)`. Last-moment destination creation and cancellation are explicitly tested. Cleanup targets only the operation-owned path.

These are process-level guardrails, **not a sandbox or hard CPU/memory budget**. FFprobe must parse untrusted input before all metadata limits are known. No suspended-process cause was established. Forced termination, OS shutdown, permission/sync interference, or delayed process creation can leave temporary files; cleanup warnings remain visible. Full-decode validation establishes output readability, not universal detection of every recoverable defect in an input video.

## Automated verification — UI checkpoint

- Release build: **0 warnings, 0 errors**. Local builds use `-p:UseSharedCompilation=false -m:1`; standard CI build is unchanged.
- Full suite after UI and safety edge-case tests: **307 passed, 0 failed, 0 skipped**, 27 seconds locally. Actual pinned FFmpeg was available, including both encoder quality cases and all ten input codec/container cases.
- View-model tests: 9 passed (Unicode source/options, duplicates/busy guards, cancellation/cleanup waiting, stale progress, bilingual errors, unknown duration/no audio, invalid settings/inspection and complete error-resource coverage).
- Actual WPF resource/layout/binding test: English and Arabic, every section, 900×620, 1120×720 and 1920×1080; **no binding errors** captured. This is automated layout validation, not a substitute for real DPI/visual testing.
- Format verification and `git diff --check` passed.
- Backend checkpoint CI at `da77e86`: both push and PR workflows green; 293 tests passed (push run 39530991661, PR run 39530996018).
- UI checkpoint `929eaa9a32c30fd171266435f72f84dc86683f0c`: both push (39532826362) and PR (39532829911) CI workflows **passed**, 307 tests, zero failed/skipped (push test duration 32 s). Final standard local restore/build/test/format gate also passed: 307 tests in 27 s, build zero warnings/errors, no extra build properties required on that final run.

## Measured quality/performance sample

Local actual FFmpeg, 3-second 640×360 25fps synthetic testsrc2 input, source encoded losslessly with libx264 qp0 (1,441,343 bytes). Each time is one run including probing, encoding and full output verification, excluding fixture generation and the separate SSIM run. Not a general benchmark or target-size guarantee; hardware, content and load affect results.

| Encoder / quality | Time (s) | Bytes | Video SSIM |
| --- | ---: | ---: | ---: |
| libx264 / High | 0.667 | 416,707 | 0.998397 |
| libx264 / Balanced | 0.528 | 289,384 | 0.995743 |
| libx264 / Small | 0.521 | 166,317 | 0.987756 |
| h264_mf / High | 0.751 | 679,118 | 0.996511 |
| h264_mf / Balanced | 0.516 | 461,185 | 0.990754 |
| h264_mf / Small | 0.533 | 286,374 | 0.983504 |

Both quality-order tests passed and now print their measurements to test output for reproducibility. These figures do not establish a universal comparison between encoders or hardware acceleration.

## Windows GUI observations

Actual WPF application, not mockups:

- Native Browse selected `فيديو تجريبي\مقطع & تجربة.mp4`; source metadata showed 320×240, 3 seconds, H.264/AAC and one stream each.
- Native MP4 Save dialog created **no file** before Convert. Conversion succeeded, displayed the localized verified-success message, and left the original intact (304,721 bytes; output 149,849 bytes). Integration tests additionally compare original hashes.
- Arabic dark and English light, live language/theme changes, navigation and retained completed state were visually verified. Default window and native maximized layout rendered without overlap/crash.
- A generated 5-minute 1280×720 source displayed real percentage/elapsed progress with source/settings disabled. Cancel terminated the observed FFmpeg process (PID 49732), restored idle controls, and left no completed destination or owned temporary file.
- Closing while busy displayed the native yes/no prompt. Choosing No kept the app running; the conversion subsequently finished. Idle close exited normally.
- A dedicated cancel-and-close run used a generated 20-minute source so it could not finish before the confirmation. Actual FFmpeg PID 14584 was running; the dialog was captured at 19% encoding. Choosing Yes exited both the app (PID 40224) and the encoder. The expected `close-20min_converted.mp4` **did not exist** afterward; owned temporary-file count was **zero**. Earlier five-minute attempts completed before confirmation and are not counted as cancellation evidence.
- Source/quality combo boxes, native picker/save dialogs and keyboard shortcuts were exercised. Native modal accessibility snapshots sometimes lagged; screenshots and keyboard interaction confirmed actual state.
- Manual image regression: existing Images page inspected a 96×64 PNG and successfully converted it to JPEG through the native destination dialog, displaying its original verified-success status. Source SHA-256 matched the original fixture; the video GUI source hash also matched its original fixture after conversion. Full existing image automation remained green.
- Visual checks at 125%/150% DPI and actual minimum-size resizing have **not** been performed. An attempted border drag did not resize the native window and is not claimed as a passed check. Automated minimum/default/large layout checks passed; default and maximized GUI were observed.
- One build attempted while the test app was running failed on the expected apphost file lock. After normal close, Release build succeeded. This is distinct from Claude's earlier unexplained suspended-process stalls; those were not reproduced in the successful bounded runs here. No antivirus or OS security settings were changed.

Actual captures: `docs/screenshots/23-phase3-arabic-dark-success.png`, `24-phase3-english-light-success.png`, `25-phase3-real-progress.png`, `26-phase3-cancel-to-close.png`.

## Deferred / release considerations

No Phase 4, installer/updater, shell integration, telemetry, cloud processing, batch UI, target-bitrate mode, HDR/color-managed fidelity guarantee, metadata/subtitle preservation, or extra output formats. Distribution licensing and a reproducible/minimal offline binary bundle require owner review before release. Media Foundation behavior is machine/build-dependent; current selected build prefers tested libx264.
