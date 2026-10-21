# Phase 3 — video conversion validation

## Backend checkpoint — 2026-10-21

Continues Claude's implementation on `feature/phase-3-video-ffmpeg`, Draft PR #10. No Phase 4 work.

- Reproduced the old playlist assertion locally: 8 passed, 1 failed in the focused nine-case run.
- The pinned FFprobe rejects the disguised `.mp4` playlist with `moov atom not found` and `Invalid data found when processing input`, not `not on whitelist`. The test now expects `InvalidOrCorruptVideo`. Production file/protocol/demuxer restrictions are unchanged.
- Replaced the cancellation test's system-wide process count with the actual encoder's retained process handle. It must exit before the operation returns; destination and owned temporary files must be absent, and cleanup must be confirmed.
- Unconfirmed process termination now surfaces explicitly rather than looking like clean cancellation or timeout. Late process creation is marked unconfirmed; its continuation still attempts termination. No destination is finalized in that state. Owned temporary cleanup remains best effort and is reported honestly.
- Restore and Release build succeeded locally (0 warnings/errors), using `-p:UseSharedCompilation=false -m:1` for the build.
- Full local suite: **293 passed, 0 failed, 0 skipped**, 26 seconds, including actual FFmpeg integration tests. `--blame-hang-timeout 90s --blame-hang-dump-type none` was enabled. No local freeze occurred in this run. This does not establish the cause of earlier stalls.
- UI completion, GUI checks, performance observations, and final CI evidence follow below when actually performed.

## Tools and distribution boundary

Development/CI build: gyan.dev FFmpeg **9.0.2 Essentials**, archive `ffmpeg-9.0.2-essentials_build.zip`, 114,768,076 bytes, SHA-256 `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`. `tools/Get-FFmpeg.ps1` verifies the download before extraction. Application runtime never downloads anything or searches PATH.

Resolution order: `<application>\ffmpeg\ffmpeg.exe` plus `ffprobe.exe`, then absolute `ORBITCONVERT_FFMPEG_DIR` (development/CI). Future offline packaging must bundle the engines and runtime; no installer is implemented here.

The selected development build enables GPL/version3 and includes libx264/libx265. Final distribution licensing is **not approved by this checkpoint**. Owner review must cover exact binary/source correspondence, redistribution notices/source obligations and codec patent considerations. An LGPL-compatible Media Foundation build or a minimal reproducible FFmpeg build are possible alternatives, not decisions made here. See the existing handoff for the initial technical assessment; obtain appropriate licensing advice before shipping.
