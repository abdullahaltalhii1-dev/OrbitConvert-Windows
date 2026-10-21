# Phase 4 — audio conversion and extraction

Base: merged Phase 3 PR #10, main `d298a144051a886f527c4aa006c91b81dc7dfd8c`. Active developer: Codex, per the owner's subsequent ownership clarification. Dedicated branch: `feature/phase-4-audio-ffmpeg`; Draft PR #11. Earlier checkpoints below are historical evidence; final verification is recorded separately.

## Backend checkpoint

- Reuses the existing pinned FFmpeg 9.0.2 Essentials binaries, FfmpegLocator and fail-closed ProcessRunner, including job containment, cancellation, startup/runtime/inactivity timeouts. No new engine download or NuGet dependency.
- Encoder detection adds libmp3lame, pcm_s16le and flac independently of video encoders. An audio-only encoder-list test still runs actual audio conversion with real FFmpeg; it is not a claim that a custom audio-only binary build was distributed.
- Shared semaphore serializes audio and video service operations; it is not a batch queue. Existing video progress parsing was extracted into a small shared helper; the existing VideoProgress API and its tests remain intact.
- Actual-content probing accepts MP3, WAV, FLAC, ADTS AAC, M4A/AAC and Ogg/Vorbis, plus the previously restricted video demuxers for extraction. Video extraction uses the default audio stream, otherwise the first audio stream. Cover art, additional audio, video, subtitles, attachments and data are not copied.
- Source codecs currently allowed: mp3, pcm_s16le/24le/32le, pcm_f32le/64le, flac, aac, vorbis, opus, ac3. Only mono/stereo accepted, with no unnecessary channel change. Unsupported layouts are rejected, not downmixed silently.
- MP3 uses libmp3lame CBR 128/192/320 kbps. These presets require 32/44.1/48 kHz: preserving other rates is rejected rather than letting the encoder silently choose another rate/bitrate. Explicit output rate options are preserve, 44.1 kHz and 48 kHz.
- WAV is signed 16-bit little-endian PCM. Converting higher-precision/float input to WAV deliberately quantizes to 16 bits; it is not lossless relative to those sources.
- FLAC levels 0–8 affect compression cost/size, not intentional codec loss. Integer PCM16/24 can remain exact with no resampling; float PCM and integer precision above 24 bits are rejected for FLAC. Lossy inputs can be stored as 24-bit FLAC but cannot regain lost quality. Resampling deliberately changes PCM samples.
- Metadata policy: remove source tags and cover art, chapters and extra streams. Encoder/container-generated technical fields may remain; no general metadata-preservation promise. Tags/cover exclusion are tested for each output format.
- Output is written only to an operation-owned CreateNew temporary file; actual container, single audio stream, codec, channels, rate, MP3 bitrate/lossless precision and duration are checked, followed by full strict audio decode. Finalization is File.Move(overwrite:false), with a final cancellation check; originals are read-only and never overwritten.
- Limits: 32 GiB input, 12 h duration, 32 streams, 8–192 kHz input, mono/stereo. Same 60 s probing, 120 s inactivity, 24 h stage runtime and existing startup/termination grace behavior as video. File-only protocol and explicit demuxer whitelists exclude playlists/network URLs. These checks are not a hostile-media sandbox or a hard memory/CPU cap.
- Focused initial real/native audio run: **51 passed, zero failed/skipped** (includes existing tests matching Audio). Later-expanded/full-suite counts follow when executed. No manually verified audio UI yet; the audio placeholder is not enabled by this backend checkpoint.
- Expanded backend gate: **377 passed, 0 failed, 0 skipped**, 58 s, including all 310 prior regressions, audio conversion in both lossy/lossless directions and same-format cases, all existing verified video container fixtures for extraction, selected-track PCM comparison, stereo/16-/24-bit fidelity, metadata/cover exclusion for all outputs, progress, actual cancellation/timeout, process failures, malformed input and file safety. Release build had zero warnings/errors. Automatic formatting was applied; final format verification follows the UI checkpoint.

## Distribution and remaining work

The application-local `ffmpeg` resolution and development override are unchanged. Future installer must bundle runtime/engines for offline use, but no installer is in this phase. The existing GPL-enabled development-build redistribution assessment and owner-controlled licensing decision remain as documented in phase-3-video-validation.md. No license selection is made here.

Remaining at the backend checkpoint: real Audio workspace, complete bilingual resources and three-engine UI busy/close protection, ViewModel/layout tests, final safety/format/fidelity coverage, performance observations, actual Windows GUI interaction where available, final quality gate and both GitHub workflows. No Phase 5 or later work.

## UI checkpoint

The real Audio workspace is now wired to the shared intake: explicit inspection, actual stream metadata, MP3/WAV/FLAC selection, format-specific settings and help, native destination dialog (no precreated file), real progress, cancellation and verified-success/errors. English/Arabic resources and RTL/LTR follow the existing shell. Images, Video and Audio disable conflicting UI operations; media services also share the one-operation semaphore. Audio closing uses cancel-and-wait or keep-waiting, while image closing retains its prior behavior.

- Local Release build: zero warnings/errors. **387 tests passed, 0 failed/skipped**, 61 s with real FFmpeg required. Focused VM/localization/actual WPF layout-binding run: **17 passed**. The WPF test covers every section, both languages and min/default/large logical sizes; it is not a manual DPI test.
- Format verification and diff whitespace checks passed.
- Backend checkpoint CI: push passed; PR run had one old video random-noise classification failure (InvalidOrCorruptVideo versus UnsupportedFormat). Input was still safely rejected. That regression now uses a seeded reproducible noise fixture; no production security restriction or rejection assertion was relaxed. The focused input cases and full local suite pass.
- Both push and PR CI passed on UI checkpoint `2bb7c43b1ae38a69fa03ffe12d24e0d223c1b0b1` (runs 39539849112 and 39539854270). Final verification follows below.

## Measured synthetic sample

Actual FFmpeg 9.0.2 Essentials. One 20-second stereo PCM16 WAV, 48 kHz, 3,840,078 bytes; explicit resampling to 44.1 kHz. Times include probe/encoding/full-decode validation, exclude fixture creation. One measurement each, not a universal performance/quality benchmark or listening test.

| Output | Settings | Seconds | Bytes | Validation |
| --- | --- | ---: | ---: | --- |
| MP3 | 192 kbps CBR | 0.900 | 481,488 | correct codec/rate/channels/bitrate and full decode |
| WAV | PCM signed 16-bit | 0.674 | 3,528,044 | correct PCM/rate/channels and full decode |
| FLAC | level 5, 16-bit | 0.574 | 1,125,497 | correct precision/rate/channels and full decode |

Separate no-resampling tests compared original WAV data chunks (read without FFmpeg) with every decoded output PCM byte for mono/stereo PCM16 and FLAC PCM24; all matched. MP3 is lossy and no byte-exact/lost-detail-restoration claim is made. Extraction tests select the default audio track and compare lossless output with that selected track, not with another audio stream. Decoder validity was tested; subjective speaker/headphone playback has not been performed.

## Actual Windows GUI verification — 2026-10-21

The local Windows desktop was available; these were real interactions with the Release executable, not cloud/Linux or generated mockups. The computer-use skill was used for native dialogs and app interaction. App-specific screenshots contain only generated, non-sensitive test samples.

- Launched the application, selected generated WAV/video samples through the native file picker, including Arabic names, spaces and `&`.
- English / dark: inspected a 20-second stereo PCM16 WAV at 48 kHz, chose a new MP3 destination through SaveFileDialog and converted at 192 kbps. The save dialog did not create the destination before conversion. Actual saved-and-verified success appeared.
- Selected that existing destination again. Conversion reported DestinationExists; SHA-256 of the existing output stayed `EDDCCCF42736D0DECD62712A8B841E522445720DEFDD1A74176FFDF476200124`.
- Switched language to Arabic and theme to light at runtime. Inspected a three-second H.264/AAC video, displayed selected audio stream #1, mono/44.1 kHz and extraction mode, then extracted to FLAC with actual Arabic saved-and-verified success.
- Inspected plain text named invalid.mp3. Displayed a nontechnical Arabic invalid/unsupported/corrupt error; settings and conversion stayed disabled.
- Converted a generated two-hour stereo PCM16 WAV. Observed advancing actual FFmpeg encoding percentage, elapsed time, disabled settings/destination/repeated conversion, and an enabled Cancel button. Cancel returned to idle with an explicit no-final-file message. Owned encoder PID 24052 exited; final output was absent and owned temporary-file count was zero.
- Started again, navigated to Video while audio was active and verified the Video panel was disabled. Alt+F4 displayed the Arabic audio cancel-and-close prompt. Confirming Yes waited for cleanup and closed the application. App PID 47936 and owned encoder PID 17916 exited; no final output or owned temporary files remained.
- The long original's SHA-256 before and after both cancellation paths was identical: `BCE20313CC908E3387F56991968D615A56BF155BBD08688A51DD9DD80719699F`.

Screenshots in docs/screenshots:

- `27-phase4-english-dark-mp3.png` — MP3 saved and verified.
- `28-phase4-arabic-light-extraction.png` — FLAC extraction saved and verified.
- `29-phase4-progress-cancel.png` — actual progress and busy controls.
- `30-phase4-cancel-to-close.png` — native close-while-busy prompt, with Video disabled behind it.

Manual limits: not a complete screen-reader audit, not a 100/125/150% OS DPI matrix, and no new manual min/max-size sweep or idle-close repetition. Keyboard/tab/native-dialog/close interactions and normal process exit after cancel-and-close were exercised. Automated STA WPF tests cover both languages and minimum/default/large logical sizes, with binding-error collection; these are not manual DPI results. WAV GUI conversion and additional codec/bitrate combinations were covered by real automated integration tests, not manually clicked individually.

## Remaining limitations

- One source at a time; default/first audio track only, no manual track picker, surround downmix, batch queue, editor or audio preview/player.
- FFprobe metadata inspection is not a complete source decode. Strict encoding and full candidate decode reject detected damage; no claim of detecting every possible corruption or of OS-level sandboxing.
- Direct real fixtures verify MP3, WAV, FLAC, AAC/M4A and Ogg/Vorbis inputs plus existing video extraction fixtures. The codec allowlist is a safety boundary, not a promise of support for every possible stream/container combination or every installed FFmpeg build.
- Float PCM / integer depth above 24 bits is not supported for FLAC. WAV output quantizes to 16 bits. No restoration of lost quality or broad metadata preservation.
- Existing startup/kill grace limitations and fail-closed job-assignment behavior remain unchanged. Unconfirmed process termination is reported as ProcessTerminationFailed with a cleanup warning, not successful cancellation.
- GPL/version3 development build and final redistribution approval remain pending owner decision. No runtime/tool auto-download, packaging, new dependencies, security-setting changes, or Phase 5+ functionality.

## Final local quality gate

After GUI testing and closing the application, all required commands passed using the installed `C:\Program Files\dotnet\dotnet.exe` (dotnet is not on this shell's PATH):

- `dotnet restore OrbitConvert.slnx` — passed, dependencies current.
- `dotnet build OrbitConvert.slnx -c Release --no-restore` — passed, zero warnings/errors, 3.66 s.
- `dotnet test OrbitConvert.slnx -c Release --no-build` — **387 passed, 0 failed, 0 skipped**, 1 min 3 s. Pinned real FFmpeg required via ORBITCONVERT_REQUIRE_FFMPEG=1; all previous image/video/native-process regressions included.
- `dotnet format OrbitConvert.slnx --verify-no-changes` — exit 0.
- `git diff --check` — exit 0. Git's LF-to-CRLF notice is informational, not a whitespace defect.

The final evidence/handoff commit is documentation and screenshots only; final push and pull-request CI results are reported on Draft PR #11 after pushing it. No merge or next phase is authorized by this document.
