# Phase 5 — Documents and Markdown (implementation in progress)

Owner-authorized scope follows merged Phase 4 PR #11 / main d2a09ae88f76384108f80d1b50a62fd1eeaf5846. One active writer: Codex; branch feature/phase-5-documents-markdown. No PDF/Phase 6, batch processing or installer.

## Engine decision

Pandoc **3.12.1**, official Windows x86_64 ZIP, **42,154,914 bytes**, SHA-256 **6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587**. Official release API provided version, size and digest. tools/Get-Pandoc.ps1 verifies the archive and extracts locally, without installation or PATH changes. Runtime resolves application-local pandoc/pandoc.exe first, then absolute ORBITCONVERT_PANDOC_DIR for development/CI. Never downloads at runtime. Only this exact version/API is accepted.

The owner's current prompt permits the smallest appropriate engine combination. Actual experiments and the first 21 passing tests confirm this Pandoc includes **both RTF reader and writer**, DOCX and ODT. LibreOffice was evaluated as the heavier alternative but is **not required or installed** for the verified semantic conversion matrix below. This does not promise LibreOffice/Word-equivalent advanced layout fidelity or implement the older roadmap's PDF tasks; the current scope explicitly defers PDF.

Primary references: [official release](https://github.com/jgm/pandoc/releases/tag/3.12.1), [input/output formats and sandbox](https://pandoc.org/MANUAL.html), [Pandoc licensing](https://github.com/jgm/pandoc/blob/3.12.1/COPYING.md). Pandoc is GPL-2.0-or-later; future distribution must preserve notices and meet exact binary/source obligations and third-party notices. No final license/redistribution decision for OrbitConvert is made here. The official ZIP contains the executable and upstream documentation/licenses; preserve required upstream notices/native files when packaging. No new NuGet dependency.

## Verified initial matrix

| Input | Outputs |
| --- | --- |
| DOCX | ODT, Markdown, TXT |
| ODT | DOCX, TXT |
| RTF | DOCX, TXT |
| Markdown | DOCX, HTML, TXT |
| HTML | Markdown |
| UTF-8 TXT | Markdown |

All 12 directions actually passed with real Pandoc and Arabic/mixed-language fixtures. No other direction is exposed. Plain-text types require .md/.txt/.html/.htm to disambiguate their intent **and** strict UTF-8/text validation; office package and RTF recognition use actual content, not just extensions. Text is literal when TXT is converted to Markdown. Markdown flavor is Pandoc Markdown with raw HTML/TeX, YAML metadata and title blocks disabled; common headings/emphasis/lists/links/tables/code/quotes/rules are covered. Exact round-trip layout/extensions are not promised.

## Safety and verification design

- Bounded 25 MiB source/output, 64 MiB expanded packages/AST, 512 package entries, 250,000 AST nodes and two-minute tool stages. ZIP entries are bounded while decompressing and CRC checked. XML forbids DTD/external resolution. Reject traversal/duplicate/symlink entries, macros/embedded OLE/field instructions and non-hyperlink external OOXML relationships.
- Source stays open read-only with FileShare.Read and is copied into an operation-owned GUID workspace; only that copy reaches Pandoc. Dedicated --data-dir prevents implicit user templates. No shell, user filters, PDF engine, scripts or automatic HTML opening. Reuses unchanged fail-closed Windows JobObject ProcessRunner and bounded termination.
- Every read/write uses --sandbox and --fail-if-warnings. Pandoc's sandbox restricts reader/writer IO to explicit files; it is **not** an OS sandbox and does not constrain arbitrary filters (none are accepted). Parse to JSON first; remove source metadata/untrusted attributes and reject raw/unknown AST nodes, unsafe links and external images. Retain only bounded validated PNG/JPEG data URIs or embedded images extracted into the owned media directory. Never copy adjacent resources or fetch URLs. Ordinary web hyperlinks are retained as links, not followed.
- Output must pass actual format/package checks and a second sandboxed read. Preserve meaningful source text in semantic order (Unicode normalized letter/number/mark subsequence, allowing writer-generated labels/URLs) and office heading/table/list counts; integration tests also assert actual Arabic strings/structures. This check is not proof of exact formatting equivalence. Final move uses overwrite:false; owned-workspace cleanup errors and unconfirmed termination are explicit.
- DOCX generation applies per-paragraph w:bidi and Arabic run w:rtl/complex-script font/language properties without changing text order. HTML uses per-block dir/lang; TXT/Markdown stay UTF-8 in semantic order, with renderer-dependent bidi. Output package/RTF images may be preserved only when validated PNG/JPEG; no arbitrary SVG/WMF/OLE or linked image fetch.

## Checkpoint results / remaining work

Initial backend: Release build zero warnings/errors; **21 focused document tests passed**, no skips, 15 s, real pinned Pandoc. First run caught an unhandled InvalidDataException for a corrupt ZIP; fixed and regression passed. Both initial backend CI events passed at 7668c843055b60a92e3f669474d829c0fdfae566 (39585724170 / 39585730167).

UI/security checkpoint: complete bilingual Documents workspace, all-engine busy guards, safe close, native dialog boundary and MVVM tests. **48 focused document/localization/actual WPF binding/layout tests passed**, zero skipped, 18 s. Earlier full suite **424 passed**, zero failed/skipped, 1 min 12 s. Additional regressions verify embedded PNG bytes through DOCX and ODT, literal TXT, CRC/expanded package limits, malicious links, multiple languages, actual cancellation/timeout with a retained owned Pandoc process handle, finalization race and corrupted output. The cancellation fixture is a generated bounded 16 MiB code block: the tiny initial fixture exited before the runner's polling interval, so it could not establish a timeout. A temporary test-helper observation bug (reading --data-dir on a --version request) caused two local failures; corrected, then the 48-test focused run passed. No production timeout was relaxed.

An actual retained-fixture run in the repository's OneDrive folder reproduced UnauthorizedAccessException reopening a Pandoc-created temporary DOCX, plus cleanup access denial. Root cause (sync/security/other host behavior) was **not established**. No protection settings were changed. Parser work now stays under the OS temp directory; only an owned .tmp is staged beside the destination after verification. The same fixture then converted successfully with confirmed cleanup. Two early operation-owned sample workspaces remain under ignored artifacts/phase-5-documents/GUI مستندات because deletion was denied; they contain only generated samples, not user originals.

Generated DOCX has per-paragraph/run direction and correctly ordered OOXML properties; ODT adds paragraph-specific writing-mode styles. Output text comparison excludes image alternative text, which office readers/writers may not preserve; image count and actual embedded PNG bytes are separately tested. Alt-text, advanced positioning/styles, pagination/fonts, complex fields, tracked changes, and exact paragraph alignment/layout are not promised. No output may be claimed visually readable solely from these structure checks; real Windows/Word inspection and final full quality gate/CI are still pending at this checkpoint.

Latest UI gate: restore, Release build (0 warnings/errors), **424 passed / 0 failed / 0 skipped**, 1 min 15 s, format verification and diff check passed with both real engines required. An additional live loopback-listener regression passed separately (1 test, 332 ms): an external image was rejected and no network connection was attempted. Final suite total is expected to be 425 but is not claimed until actually run.

The actual WPF executable launched and showed the new Arabic dark shell/navigation. Further native-dialog/Word GUI checks were interrupted by an unrelated IDM update/confirmation dialog. The owner has been asked to dismiss it manually; no update, privacy/security change or warning acceptance was performed. Detailed conversion/close/DPI GUI results remain unverified until this obstacle is removed. Automated all-section WPF binding/layout tests are separate evidence, not a substitute for these interactions.
