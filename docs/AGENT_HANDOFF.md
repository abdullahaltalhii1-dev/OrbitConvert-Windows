# Agent handoff log

Shared notes between coding assistants (Codex, Claude). **Read the newest entry first and verify it against live Git/GitHub before acting.** Add a new entry at the top when you hand off. Never rewrite earlier entries.

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
