# Phase 4 — audio conversion and extraction (in progress)

Base: merged Phase 3 PR #10, main `d298a144051a886f527c4aa006c91b81dc7dfd8c`. Active developer: Codex. Dedicated branch: `feature/phase-4-audio-ffmpeg`. This document records checkpoints; it is not a completion claim until the final gate and GUI evidence are recorded.

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
- Backend checkpoint CI: push passed; PR run had one old video random-noise classification failure (InvalidOrCorruptVideo versus UnsupportedFormat). Input was still safely rejected. That regression now uses a seeded reproducible noise fixture; no production security restriction or rejection assertion was relaxed. The focused nine input cases and full local suite pass.
- Actual Windows GUI checks and final CI are still pending at this checkpoint.

## Measured synthetic sample

Actual FFmpeg 9.0.2 Essentials. One 20-second stereo PCM16 WAV, 48 kHz, 3,840,078 bytes; explicit resampling to 44.1 kHz. Times include probe/encoding/full-decode validation, exclude fixture creation. One measurement each, not a universal performance/quality benchmark or listening test.

| Output | Settings | Seconds | Bytes | Validation |
| --- | --- | ---: | ---: | --- |
| MP3 | 192 kbps CBR | 0.900 | 481,488 | correct codec/rate/channels/bitrate and full decode |
| WAV | PCM signed 16-bit | 0.674 | 3,528,044 | correct PCM/rate/channels and full decode |
| FLAC | level 5, 16-bit | 0.574 | 1,125,497 | correct precision/rate/channels and full decode |

Separate no-resampling tests compared original WAV data chunks (read without FFmpeg) with every decoded output PCM byte for mono/stereo PCM16 and FLAC PCM24; all matched. MP3 is lossy and no byte-exact/lost-detail-restoration claim is made. Extraction tests select the default audio track and compare lossless output with that selected track, not with another audio stream. Decoder validity was tested; subjective speaker/headphone playback has not been performed.
