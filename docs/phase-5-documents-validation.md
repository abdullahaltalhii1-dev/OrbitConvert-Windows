# Phase 5 — Documents and Markdown (submitted for owner review)

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

Generated DOCX has per-paragraph/run direction and correctly ordered OOXML properties; ODT adds paragraph-specific writing-mode styles. Output text comparison excludes image alternative text, which office readers/writers may not preserve; image count and actual embedded PNG bytes are separately tested. Alt-text, advanced positioning/styles, pagination/fonts, complex fields, tracked changes, and exact paragraph alignment/layout are not promised. No output is claimed visually readable solely from structure checks; independent Word inspection follows below.

UI gate: restore, Release build (0 warnings/errors), **424 passed / 0 failed / 0 skipped**, 1 min 15 s, format verification and diff check passed with both real engines required. An additional live loopback-listener regression passed separately (1 test, 332 ms): an external image was rejected and no network connection was attempted. The subsequent full run actually passed **425 / 0 failed / 0 skipped**, 1 min 16 s, followed by successful formatting/diff verification. All 387 Phase 4 baseline tests remain included.

The actual WPF executable launched and showed the new Arabic dark shell/navigation. Initial interaction was interrupted by an unrelated IDM update/confirmation dialog. The owner was asked to dismiss it manually; no update, privacy/security change or warning acceptance was performed. Testing later resumed through visible application controls/native dialogs without answering the unrelated confirmation. Automated all-section WPF binding/layout tests remain separate evidence from the actual interactions below.

## Actual Windows / independent Word checks — 2026-10-21

- Native picker added generated Markdown/DOCX and large samples with Arabic filenames, spaces and ampersands. Explicit inspection displayed actual format, byte size and heading/table/list counts; selection did not automatically convert.
- Arabic dark: Markdown → DOCX succeeded and displayed saved-and-verified success. The native save dialog did not precreate the destination (Test-Path false before Convert).
- Arabic light: DOCX → ODT succeeded; DOCX → TXT also succeeded. Actual TXT contained the expected Arabic/English heading, ordinary text, numbers/punctuation and table content in UTF-8.
- Changed interface language live to English; DOCX → Markdown succeeded in the light theme. Choosing the same output again produced DestinationExists; SHA-256 remained `1DB71EEA0368E10D9B820E52670202D251483210EE6A63E81585E738F8880B48`.
- Opened the **actual generated DOCX and ODT in the installed Microsoft Word**, without repair dialogs or enabling any content. Visually verified joined Arabic letters, readable mixed Arabic/English headings/paragraphs, Arabic numbers/punctuation, tables, lists, emphasis and code. Arabic paragraphs were right-aligned/RTL, English paragraphs LTR. No reversal or replacement characters observed in these ordinary fixtures. ODT changed fonts/table placement/rules relative to DOCX; this is semantic conversion, not an exact-layout guarantee. Closed both without saving edits. Word screenshots were intentionally not committed because its frame displayed account information; no user documents were opened.
- Actual indeterminate processing state, disabled settings/destination/repeated Convert and enabled Cancel were observed. **Manual cancellation/close-while-busy was not conclusively verified**: the 16 MiB code-block job finished successfully before the cancel click; an 18,000-paragraph sample reached output validation rejection before the next cancel click. No false cancelled status was shown. Its original SHA-256 stayed `22E4AE4FFE5BF2F463B1E285945D6606CDDBB7ADE0A3FF4CAECE5B2B7F838825`. Alt+F4 arrived after processing and closed normally; no app/Pandoc process remained. Do not count this as a passed busy-close prompt check. Actual native cancellation/timeout/owned-process termination and ViewModel cancellation waiting are covered by automated tests instead.
- Three ordinary conversions and their native dialogs remained responsive. No manual full DPI matrix, screen-reader audit, min/max window sweep or fresh manual image/video/audio conversion regression sweep was performed. All-section, both-language, min/default/large logical-size WPF binding tests and all previous real-engine regressions passed; those are not manual DPI results.

Actual WPF screenshots (generated samples only):

- screenshots/31-phase5-arabic-dark-markdown-docx.png
- screenshots/32-phase5-arabic-light-docx-odt.png
- screenshots/33-phase5-english-light-docx-markdown.png
- screenshots/34-phase5-indeterminate-progress.png

## Final CI / review boundary

Both Windows events passed on code commit **e2894b68f0aa3765283676319168d13438f381f6**: push [39588129396](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/actions/runs/39588129396), 425 passed / zero failed/skipped, 49 s; PR [39588136895](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/actions/runs/39588136895), 425 passed / zero failed/skipped, 1 min 8 s. Both Release builds had zero warnings/errors; real Pandoc and FFmpeg were mandatory. The following documentation/screenshot handoff commit must also pass both events; final results will be reported on Draft PR #12.

No known failing code test remains. Remaining review risks are the explicitly unverified manual busy-close/cancel/DPI/accessibility checks, advanced document-layout limitations, bounded-but-not-OS-sandbox process behavior, and owner-approved final redistribution/packaging. No PDF/Phase 6 or later functionality was implemented. Keep Draft PR #12 unmerged pending owner review; passing tests are not owner approval.

## Post-merge final QA — 2026-10-21–22 (supersedes the manual cancellation gap above)

PR #12 was verified merged as main **c26f170a46b441d7ca23bd23dde7b1c8742aace3**. Local main was fetched/fast-forwarded and built in Release with no restore: **0 warnings/errors**. No confirmed defect required a code change. This follow-up changes documentation only on `codex/phase-5-final-qa`, not main; the original submitted-for-review text above is historical.

### Real Windows observations

Only safe generated documents were used. The successful cancellation fixture, `artifacts/phase-5-final-qa/جدول صالح & cancel.md`, is **24,027,484 bytes**, with a heading and 1,500 four-column table rows with long text cells. Explicit inspection succeeded within the existing size/structure limits. SHA-256 before and after every relevant operation was **D05507B1E761EC4D8CA1A864C9594B3E0FB9E529BA69A43B6008BC3D95707824**. Conversion was started through the real Documents view and native destination dialog; no mocks, suspended processes, artificial delays or changed limits were used.

| Check | Actual observation | Result |
| --- | --- | --- |
| Cancel while Convert is active | App 27116 owned running Pandoc 42264, reading the workspace copy as the initial stage of real Markdown→DOCX conversion. Clicking Cancel displayed `Document operation cancelled. No completed destination was saved.` The child and `.orbitconvert-doc-ff8a027f7bd64d3db80ad4cbffc7794a` disappeared; destination `table-cancel.docx` and destination .tmp were absent at that check; original hash matched. | Passed |
| Close confirmation and No | A new real conversion owned Pandoc 46104. Alt+F4 displayed the cancel-and-close Yes/No dialog. No dismissed it; app 27116 and Pandoc 46104 were both still running, progress remained active, and no destination existed at that observation. | Passed |
| Close confirmation and Yes | Separate attempt under app 40172, targeting new `table-close.docx`. Native Pandoc 36396 was observed running immediately before Yes. A read-only process observer retained its handle and recorded exit **-1** at elapsed **24.6189966 s**, followed by application exit at **24.625058 s**. Exact workspace `.orbitconvert-doc-ec077a34dcd745518af347bef6afd770` disappeared; destination and owned .tmp were absent; original hash matched. | Passed |
| Final state | No OrbitConvert/Pandoc process, no OS-temp `.orbitconvert-doc-*` directory, and no operation-owned destination .tmp remained. The initial English language / Dark theme was restored before normal shutdown of the layout-check instance. | Passed |

The elapsed values above are relative to starting the observer, **not conversion-duration benchmarks or UI shutdown latency measurements**. The observer only read process/filesystem state; it never cancelled/killed a process itself. The actual application performed cancellation and cleanup. These checks establish cancellation during the initial native-reader stage of the real Convert workflow, not cancellation at every possible writer/verification/finalization timing boundary.

Two preliminary attempts are explicitly **not** cancellation passes: the 23 MiB code-block conversion finished before the Cancel click; the first table operation finished before the later Yes click after No had been tested. Their valid generated `cancel-output.docx` and `table-cancel.docx` outputs were retained. Therefore the final directory containing `table-cancel.docx` does not contradict its absence immediately after the successful first cancellation. A 9,000-row sample exceeded the existing structural limit and was safely rejected; reducing row count while increasing cell lengths produced the inspected fixture above. Limits were not relaxed.

### UI coverage / remaining limits

- Visually verified limited English LTR and Arabic RTL, Dark and Light themes, Settings/Documents navigation and real document scrolling at the current default display/window configuration. No crash or overlapping body controls was observed in those views. This is a limited smoke check, not an exhaustive UI audit.
- **125% and 150% Windows display scaling were not tested.** No display/security setting was changed. Native resize gestures/menu interaction did not establish a changed window size; actual rendering at the 900×620 minimum remains unverified. The earlier automated logical-size tests are not substitutes for these manual checks.
- No new package, conversion behavior, resource policy, workflow, installer or Phase 6 feature was added. Existing document-layout, redistribution/licensing and non-OS-sandbox limitations still apply.

Focused regression command: `dotnet test -c Release --no-build --filter 'FullyQualifiedName~DocumentSafetyTests.ActualCancellationAndTimeout|FullyQualifiedName~DocumentViewModelTests'`, with real pinned Pandoc configured: **6 passed, 0 failed, 0 skipped**, 2 s. This QA run did not repeat the entire suite locally; any final documentation-PR CI results are reported separately, not invented. Generated fixtures/observer stay ignored in `artifacts/phase-5-final-qa`; no user document was modified, and no older unrelated temporary folders were deleted.
