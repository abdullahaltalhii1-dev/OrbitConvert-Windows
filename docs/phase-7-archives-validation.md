# Phase 7 archive validation — 2026-10-23

Status: implementation submitted for owner review, **not final acceptance**. Manual active-operation Cancel and Close → No/Yes timing remain unverified; do not infer those passes from automated coverage or green CI. No Phase 8, merge, packaging or system security changes are authorized here.

Delivery: [Draft PR #15](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/pull/15), initial implementation `64ce50673165f017b992f3a7ac87b03a0680b802`, reviewed baseline `467dd6078bd697216507ccc1ccae10da32af3ac7`. The security follow-up below addresses the owner's C1–C3 authorization and parent-stage cancellation test. Final-SHA Push and PR CI results are reported on the PR after completion. Keep Draft and do not merge without owner approval.

## Baseline and package decision

PR #14 was merged as `8f5ef3fec922f75ad4d48aa568e792dc34f31bfe`; its main push run [39839020095](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/actions/runs/39839020095) succeeded. This branch is `feature/phase-7-archives`, one writer. Pre-existing untracked `src/OrbitConvert.App/OrbitConvert.App_dcgdz3wr_wpftmp.csproj` is preserved and excluded from delivery.

**SharpCompress 0.50.7**, exact direct version and content hash in `packages.lock.json`, is the only added package. [NuGet provenance](https://www.nuget.org/packages/SharpCompress/0.50.7), [release](https://github.com/adamhathcock/sharpcompress/releases/tag/0.50.7) and packaged repository commit `b4af8afe6683d019d504954a3b4e4f18b06e2cd6` were checked before installation. The selected net10.0 dependency group has no additional package dependencies. No 7-Zip CLI or second archive engine is installed. This is not an assertion that every codec advertised upstream is safe/supported by OrbitConvert.

### Security advisories

- [GHSA-7jcq-g98g-83v5](https://github.com/adamhathcock/sharpcompress/security/advisories/GHSA-7jcq-g98g-83v5): advisory metadata lists no patched release, but this is stale relative to [upstream commit 2f2730fd01009b640e25b5f455beb59d7f27505a](https://github.com/adamhathcock/sharpcompress/commit/2f2730fd01009b640e25b5f455beb59d7f27505a). Both synchronous and asynchronous reader sources were checked at tags 0.50.4, 0.50.5, 0.50.6 and 0.50.7: the physical next-header bounds check before allocation is absent in 0.50.4 and present since **0.50.5**, including our pinned 0.50.7. This corrects the earlier claim that the selected release lacks that fix. The NuGet binary was not decompiled; the lock-file content hash remains pinned.
- The independent guard remains defence in depth: signature/version, start CRC, **outer** next-header physical range and **1 MiB cap**, next-header CRC and exact EOF. Existing published-PoC and <256 KiB guard-allocation tests remain unchanged. This does **not** bound sizes/counts declared inside an encoded header or all decoder allocations. Those remain dependent on the worker's confirmed Windows Job Object containment, committed-memory quota and deadline; no universal 7z safety claim.
- [GHSA-5wf8-cxh5-32fv](https://github.com/adamhathcock/sharpcompress/security/advisories/GHSA-5wf8-cxh5-32fv): truncated LZMA2 infinite loop fixed upstream in 0.50.6, included in 0.50.7. Bounded child termination still applies.
- Older traversal/TAR-link advisories were reviewed. OrbitConvert never uses library extraction-to-directory helpers or a symlink handler, and never creates archive-provided links.
- The original `dotnet list ... package --vulnerable --include-transitive` reported no vulnerable packages in that feed. Neither a green audit nor stale advisory metadata replaces source-level verification. Recheck upstream advisories before distribution or updating the pin; never remove the independent guard solely because audit is green.

### License / redistribution

The [upstream MIT license](https://github.com/adamhathcock/sharpcompress/blob/0.50.7/LICENSE.txt) requires its copyright and permission notice with distributions. Retain the pinned release's third-party codec notices and review bundled-code obligations along with the other engines before packaging. NuGet use is not distribution approval; installers and the final third-party notice bundle remain later separately approved work. No external binary download is needed at runtime for archives.

## Verified operation matrix

| Format/profile | Inspect + full content read | Extract to NEW folder | Create | Repackage to verified ZIP |
|---|---|---|---|---|
| Single-volume ZIP, Store/Deflate | Yes | Yes | Yes | Yes |
| Uncompressed TAR, V7/USTAR/GNU long names | Yes | Yes | Yes | Yes |
| Bounded 7z, actual LZMA fixture | Yes | Yes | **Not exposed** | Yes |
| Encrypted, RAR, ZIP64, SFX/multivolume, compressed/PAX/sparse TAR, links/special files | Refused | Refused | Unavailable | Refused |

Real tests write/reopen ZIP and TAR, independently read them using .NET ZipArchive/TarReader, extract and compare original bytes/SHA-256. A real test-only SevenZipWriter fixture exercises actual LZMA read/extract/repackage; that is not a product 7z-writing claim. Empty ZIP/TAR, empty/nested folders, Arabic long names and binary data are covered. The GUI also fully inspected a 31,921,561-byte 7z containing eight 30 MiB files (240 MiB total). Other 7z codecs/profiles are not exhaustively certified and may be refused by integrity/resource checks. No nested archive is automatically opened/executed.

ZIP raw central/local paths, flags, methods, offsets, sizes, CRC and descriptors must agree with each other and the library; hidden local entries, name overrides and unsupported extras/profiles are refused. TAR header checksums, bounded octal sizes, type flags and two zero end blocks are checked before library parsing. TAR has **no content CRC**: unknown payload bit flips cannot universally be detected; extracted hashes establish consistency with the decoded source, not an external authenticity claim. ZIP/7z CRC is checked where required by this supported profile; archives without usable expected integrity information can be refused.

## Safety and resource contract

| Limit | Value |
|---|---|
| Input archive, output archive, one source/expanded file | 64 MiB each |
| Actual total expanded / creation bytes | 256 MiB |
| Paths including implicit parent folders | 1,000 |
| Directory depth | 16 |
| Relative / full path | 180 / 240 UTF-16 characters |
| Expansion | 200:1 archive-wide; also per ZIP entry, 1 MiB floor |
| Worker committed job memory / deadline | 512 MiB / 2 minutes |
| Private workspace monitor | 768 MiB; 3,020 filesystem entries; 200 ms polling |
| Streaming buffer / 7z next header | 64 KiB / 1 MiB |
| Worker request / response | 512 KiB / 1 MiB |

Actual streamed bytes are checked **before writing** in addition to header bounds, including an adversarial fixture whose declared size is 1 byte but expands to 2 MiB. Temporary monitoring is polling, not an OS disk quota; transient overshoot is possible. Same-parent staging adds a bounded second copy. Filesystem metadata/open/flush calls in the parent are not hard-real-time interruptible; cancellation is checked between bounded copies and before publication. The archive worker waits for redirected stdin EOF before opening even its request. ProcessRunner releases EOF only after successful job assignment, and terminates a failed-assignment child without releasing that startup barrier. Other engines' successful startup/timeout/cancellation flow is unchanged.

### Security-review follow-up (C1–C3)

- **C1:** Real worker tests deliberately delay the parent before job assignment. With the reviewed code, the absent-request test failed because the worker exited early, and forced assignment failure failed because a response had already been written. After the barrier fix, both pass: no early request access/ready/response, forced assignment failure reports `StartFailed`, the owned child exits, source bytes are unchanged, no destination is published and the exact workspace is removed. A successful assignment releases the worker normally. Cancellation while creation is delayed at the barrier returns promptly with honestly unconfirmed termination, then the existing late-start cleanup kills the owned process without parsing.
- **C2:** Catch `OutOfMemoryException` (including derived `InsufficientMemoryException`) during archive processing and return `TooLarge`. A real, legitimate 7z fixture has a 64 MiB dictionary and a 40 MiB entry of repeated seeded-random 1 MiB blocks. It succeeds under the unchanged production **512 MiB** quota. Under a test-only **64 MiB** job quota it returns a real `TooLarge` response, exit code 1, confirmed process exit, no published destination, successful owned cleanup and identical source bytes. Removing only the OOM catch reproduces the incorrect `OutputFailure`; restoring it passes. Initial 1 MiB samples with 96/64 MiB quotas did not exhaust memory: upstream grows the dictionary lazily, so those experiments are not containment evidence.
- Not every OS/runtime failure is catchable, and an exhausted process may be unable to serialize its response. A separate real-worker forced-exit test confirms missing-response failure stays generic `OutputFailure`, not a guessed `TooLarge`; it still publishes nothing and cleans only after confirmed exit. Inspection retains its existing `UnreadableInput` fallback. No exit-code heuristic or claim of universal WER/crash-dialog suppression was added.
- **C3:** Corrected advisory status and the guard comment as described above; no dependency, security-policy or guard relaxation. Historical handoff entries remain intact; the newest entry explicitly supersedes their inaccurate advisory characterization.
- **Parent stage:** A synchronous `Verifying` callback cancels after real worker exit. The regression asserts `Cancelled`, unchanged original, absent destination/workspace/staging, preserved unrelated sentinel and no live owned worker. This adds coverage; the old behavior already passed it. The video cancellation-order fix is unchanged.
- Reviewed optional **P2–P4**, without implementing them: same-user staging mutation remains outside the absolute-sandbox guarantee; local ZIP extra-field filtering is less strict than central filtering, with decoded name/size/CRC agreement still enforced; solid 7z per-entry rereading may reach the existing deadline. Additional hardening needs owner approval.
- Six new focused native regressions passed. Full final Release/CI results are recorded below and on the PR. No new GUI evidence is claimed in this focused security task: active Cancel / Close No / Close Yes remain **NOT VERIFIED manually**. No production delay, security setting change or Phase 8 work.

Paths reject `..`, absolute/drive/UNC/device paths, ADS, DOS reserved names (including superscript COM variants), empty/dot components, invalid/control/bidi-control characters and trailing spaces/dots. NFC + Windows case-insensitive comparisons reject duplicates, ancestor aliases and file/directory conflicts. The whole operation fails on danger; no skip-and-success behavior. Root/parent/source reparse points and hard-linked source files are refused; ordinary local files in the current OneDrive checkout were tested. Cloud placeholders/aliases can be refused; this is deliberately restrictive, not transparent support for every Windows path.

Originals stay read-only and pinned through publication/cleanup. Snapshots are private and read-locked during child use. Windows handle-relative no-follow opens (`NtCreateFile`, one component at a time) avoid re-resolving held ancestors through junctions. Directory handles include actual list access and deny delete sharing; metadata-only handles did not enforce this guarantee in the initial test and were corrected. Real junction and in-place post-pin reparse mutation tests prove refusal/no outside writes. No reliance on archive names alone or PowerShell extraction commands.

New output is fully hash-verified in an owned location, flushed, then published by renaming its **held handle** relative to its **held destination-parent handle**, with replacement disabled. A racing existing file/directory is preserved, not merged or replaced. Windows requires descendant handles closed for directory rename: after final verification they close at a barrier, while root/ancestor pins remain and no further descendant IO occurs before publication. An administrator or arbitrary same-user process can still mutate accessible files after verification/publication; **this is not an absolute Windows sandbox** or protection against a compromised local account.

Cleanup is restricted to the exact operation-owned file/root, uses held handles and deletes link objects without traversing targets. Creation failure never grants ownership over a guessed name. Child exit must be confirmed before cleanup; uncertain termination preserves the private workspace and reports termination/cleanup warnings. Locked owned cleanup failure is reported explicitly while unrelated files remain untouched. There is no stale-directory sweep.

## UI / cancellation

The existing Studio design, shared file intake, all prior engine services and native Magick policy remain intact. Archives has explicit inspection, content paths/sizes/counts, separate creation list/optional folder, native new-destination dialog, four translated modes, real indeterminate stages, Cancel and success/error/cleanup status. Paths/format identifiers remain LTR; the container follows Arabic/English. Busy coordination covers all six engines without a queue. Selection alone does no decoding. Closing integrates Yes → cancel/await cleanup/close and No → keep running; warning/uncertain cleanup prevents automatic close.

Actual UI verification caught a mode label displaying a CLR type name; an explicit localized item template fixed it. The WPF regression now checks the visible TextBlock, not just SelectedIndex. Full-suite verification also exposed a pre-existing video cancellation status race: a synchronous cancellation completion could be overwritten by the pending message. Two deterministic new tests failed before the narrow ordering fix and pass afterward (archive and affected video path); prior test/security expectations were not changed. No other engine was refactored.

## Actual Windows GUI evidence

Real Release application, not mockups:

- Native multiple selection: `English notes.txt` (85 bytes) and `مرحبا mixed.txt` (164 bytes).
- Created `artifacts/phase-7-archives/gui/Created Arabic.zip` (481 bytes), observed real busy stage then verified success, explicitly inspected two names/sizes.
- Extracted to new `artifacts/phase-7-archives/gui/Extracted عربي`, observed verified success.
- Windows Explorer independently opened the ZIP and displayed both Arabic/English names, then independently displayed both extracted files. No content execution or viewer save.
- SHA-256 original/extracted pairs identical: English `67BAD0B0430465865096704638F23AABD230124E31953DA5449BCEDC9BC64056`; Arabic `4C06589E532071A05A48F0DAC9B4F5FEB1B6099E551D6E49677E9DFD79F027DD`. ZIP hash `451BF53011E8AA7262FC13799ED06C6EFCC7859233EFA419210277D46CA60E55`.
- Switched populated Archives via Settings to Arabic without losing mode/content/destination; inspected Dark and Light, default and maximized windows. Final Release relaunched and inspected the real large 7z with correct mode labels. No crash observed; idle close exercised.
- Native screenshots committed: `35-phase7-zip-created-english-dark.jpg`, `36-phase7-extracted-arabic-dark.jpg`, `37-phase7-arabic-light-contents.jpg` in `docs/screenshots`. They precede the cancellation-order-only fix; layout is unchanged. No unrelated viewer/account screenshots exported.

**Not passed:** interactive Cancel and busy Close → No/Yes. Multiple attempts with a real 900-file 225 MiB ZIP and 8-file 240 MiB LZMA 7z finished before input reached the active control. One default-window Cancel target was offscreen and the automation correctly refused the click; no bypass was used. A close attempt occurred after completion and only proved idle closing. Observing a worker/busy indicator in an earlier snapshot is not proof that a later Cancel interrupted it. Automated native cancellation/deadline/process-exit/cleanup and ViewModel waiting tests passed separately. Repeat the three manual active-operation cases for owner acceptance; do not introduce production delays to manufacture evidence.

The large ZIP source SHA stayed `754EC3C55E9DBC9DA99BC6F79AE6DBD560A1934FE7013C60A611929BB6604930`; bounded 7z SHA `6ADA758D7B47CE13FC4BD97AA28EC9E1E80B2A15DB3FF01664C4871EF36850C7`. Generated QA originals/results remain ignored under `artifacts/phase-7-archives/gui`; they are not customer files. An initial 127 MiB 7z fixture exceeded the input limit and was not used for a supported-operation claim.

Manual minimum-size interactions/full accessibility audit are not completed. Actual default layout uses vertical scrolling; the real WPF test covers all four modes/both languages at logical 900×620, 1120×720 and 1920×1080, with binding-error capture. **125%/150% DPI is explicitly deferred by owner to Phase 9**, not passed. No display/security/Norton settings were changed.

## Quality gate

Final local commands:

```powershell
dotnet restore OrbitConvert.slnx
dotnet build OrbitConvert.slnx -c Release --no-restore
dotnet test OrbitConvert.slnx -c Release --no-build
dotnet format OrbitConvert.slnx --verify-no-changes
git diff --check
```

Security follow-up: restore passed. Release build **0 warnings / 0 errors**. Full suite **540 passed / 0 failed / 0 skipped**, **56 seconds**, with real pinned FFmpeg/Pandoc/qpdf configured and media/document tests required. All previous 534 tests are retained; six new native containment/memory/parent-cancellation tests pass. TRX: ignored `artifacts/phase-7-archives/test-results/phase7-security-fixes.trx`. Format verification and diff checks passed. Git printed a Markdown LF→CRLF normalization notice, not a build warning or whitespace error. Final commit Push/PR CI must be checked live; local results are not CI evidence. The baseline's 534-test evidence remains in `phase7-acceptance.trx` and the preceding handoff.

Earlier failures were not hidden: native directory pins/relative rename mechanics, TAR Arabic encoding, ZIP directory writer header inconsistency, real mode label binding and a test-only separator expectation were corrected during development. A full run exposed the cancellation-order race (531/1/0); dedicated reproductions failed 0/2 before correction. Another invocation used forward-slash engine override paths and failed qpdf version matching (490/44/0); rerun with native Windows override paths passed the complete suite without PDF changes or weakened assertions. The standalone ignored fixture helper also needed reference-path/API corrections; its compiler errors are not an application build result.

## Remaining acceptance / limits

Owner review should examine the Windows native handle boundary and strict profile restrictions, outer-header defence in depth, residual internal allocation risk and tested worker quotas, then observe manual active Cancel/Close No/Yes. Green automated tests alone do not complete Phase 7. No general archive compatibility corpus, benchmark claim, password extraction, RAR/7z product writing, timestamp/ACL preservation, long/network/device-path guarantee, full DPI/accessibility pass, packaging approval or Phase 8 work is claimed.
