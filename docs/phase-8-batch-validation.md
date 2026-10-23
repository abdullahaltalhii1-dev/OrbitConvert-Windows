# Phase 8 — unified batch queue

Branch: `feature/phase-8-batch-queue`, based on latest main `c412b0b729a181c3cbfe2f19775141e582e85d31` (merged PR #15). Main push CI [39895690277](https://github.com/abdullahaltalhii1-dev/OrbitConvert-Windows/actions/runs/39895690277) passed. Preflight read AGENTS, latest handoff, architecture, roadmap v3.0 Phase 8 and existing services/view models/closing/localization. Preserved the unrelated untracked `OrbitConvert.App_dcgdz3wr_wpftmp.csproj`.

Plan: one session-only FIFO orchestrator; immutable per-job presets and source metadata; a small adapter directly invoking existing services; bilingual Batch workspace; deterministic queue tests plus actual engine integration and Windows GUI evidence. No added package, native binary, encoder, persistence or process pool.

## Reviewed capabilities

| Engine | Batch operations | Source acceptance / validation |
|---|---|---|
| Magick.NET | PNG, JPEG, WebP, BMP output | Existing static 8-bit PNG/JPEG/WebP/BMP guards. Quality 90, no resize; JPEG/BMP use existing white alpha background and orientation normalization. |
| FFmpeg video | MP4 H.264/AAC | Existing MOV/MP4/Matroska/WebM/AVI demuxers/codecs; Balanced quality, original height. Probe and complete output decode unchanged. |
| FFmpeg audio | MP3, WAV, FLAC; existing audio extraction | Existing supported audio/container/codec matrix. MP3 192 kbps, WAV 16-bit, FLAC level 5, original sample rate; existing precision/rate checks apply. |
| Pandoc | Existing document matrix only | DOCX → ODT/Markdown/TXT; ODT/RTF → DOCX/TXT; Markdown → DOCX/HTML/TXT; HTML → Markdown; TXT → Markdown. |
| PDF | Individual panel only | Merge/reorder/page ranges/render/text require page selections or multiple-source/output ownership. Not forced into this single-file queue. Existing service remains available unchanged. |
| Archives | Individual panel only | Create accepts multiple sources/folders; extract publishes a complete new directory; repack has archive-specific inspection/ownership. Kept outside this single-file queue; no new archive conversion capability. |

Filename extensions are admission hints, never content validation. Each engine still inspects actual contents. Unsupported selections are rejected atomically on Add. Multiple compatible files can be added per preset; clear intake and add another group/preset before Start to build mixed-engine queues. Output preset/default settings are frozen per job and shown in the UI; custom resize/bitrate/page settings stay in individual panels.

## Scheduling and state

- `BatchRequest` snapshots source, destination, source length/last-write UTC and validated preset/options. `BatchJob` adds stable GUID, attempt, status, actual progress, localized error key, result and cancellation/cleanup state.
- `BatchViewModel` owns a read-only observable job list and FIFO loop, awaiting exactly **one** adapter call. Default maximum **100** retained jobs; internally configurable 1–1000 by constructor. Clear completed/errors explicitly to release slots. No automatic retry or scheduling on selection.
- Pending → Inspecting → Running → Verifying (when engine reports it) → Completed/Failed/Cancelled. Pending jobs may cancel directly. Stages do not move backwards within an attempt; reports are ignored after completion/cancellation or an attempt change.
- `BatchExecutor` calls engine services, never conversion ViewModel commands. Media fractions come from real FFmpeg reports; document and image work is indeterminate. Overall counts explicitly distinguish completed, active, pending, failed and cancelled.
- MainViewModel disables individual panels during batch and batch during any individual operation; a latched batch cleanup/termination warning also keeps individual engines disabled for the session. Navigation, language, accents and themes remain usable. Preset choices/job IDs are stable across live localization, including unloaded views.
- All existing engine locks, Magick native initialization/policy, FFmpeg/Pandoc containment and output verification remain. No quota changes. One job bounds concurrent temporary/storage/memory use to that engine's existing limits; this is not a new global OS disk quota or performance claim.

## File safety

- Add validates local absolute paths ≤240 UTF-16 characters, existing regular non-reparse sources, compatible presets, destination directory, duplicates and Windows case-insensitive conflicts. No fake reservation/output is created. Planned output is `<source stem>-orbitconvert.<extension>`.
- All pending paths are revalidated before scheduling, and each source/destination immediately before execution. Missing/changed sources fail individually; normal per-file failures do not stop later valid jobs. A source length/last-write change is detected; the adapter holds a read-only, read-shared source handle throughout processing and rechecks its stamp after opening.
- The metadata stamp is not a cryptographic identity check against an adversary restoring both size and timestamp before execution; content integrity/format comes from the actual engine. The source is protected from normal Windows writes/replacement while held. This is not a sandbox against a compromised account/admin.
- Existing services own all temporary writing, validation, flush and no-overwrite publication. A racing destination is preserved and reported as a job failure. Successful earlier outputs are never deleted on later failures, cancellation, Clear or Retry.
- Retry is explicit for Failed jobs only, with fresh source metadata and collision revalidation; preserves job ID. Cleanup/termination uncertainty latches a session warning, stops further scheduling and blocks retry/start even if error rows are cleared. Unknown exceptions fail conservatively with cleanup uncertainty; ordinary missing-input errors remain per-job failures.

## Cancellation and closing

Pending cancellation never invokes the engine. Active cancellation signals only that job's token, awaits its service/cleanup, then continues other pending work. Cancel Queue marks all pending cancelled before signalling the active job and never schedules another. If a fully verified publication wins the cancellation race, its genuine success remains Completed.

ImageConversionService adds a token-aware overload while preserving the existing interface and single-image API. Bounded native calls cannot be forcefully interrupted in-process; cancellation waits for the current call, checks between stages/before publication, removes only its owned temporary and preserves cleanup warnings. This is not immediate native termination. No image policy change.

MainWindow uses its existing Yes/No busy-close pattern for batch: No leaves work running; Yes cancels pending/active work and awaits idle/cleanup, then closes only without an uncertainty warning. Real GUI confirmation timing must be recorded separately from ViewModel tests.

## Verification evidence

Local Release restore/build/test/format/diff checks run on Windows with the existing pinned FFmpeg 9.0.2, Pandoc 3.12.1 and qpdf 12.4.2 configured; no tools were installed or replaced. The final test totals are recorded below and in the delivery comment. Final-SHA Push/PR CI run links/results belong in the PR delivery comment, avoiding a documentation-only SHA/CI cycle. No pending check is a pass.

Final local gate: restore passed; Release build **0 warnings / 0 errors**; **567 passed / 0 failed / 0 skipped**, duration **1 min 1 s**. All prior 540 tests retained; 27 added cases. `dotnet format OrbitConvert.slnx --verify-no-changes` and `git diff --check` both passed. Ignored TRX: `artifacts/phase-8-batch/test-results/phase8-final.trx`.

Focused tests already exercised FIFO, mixed presets, normal failure continuation, queue limit, duplicate/collision refusal, Unicode paths, stable IDs/settings, pending/active/all cancellation, cleanup waiting, duplicate Start, stale reports, explicit retry, changed/deleted sources, destination races and uncertainty latching. Real engine tests exercise images (independent WIC decode), media, Pandoc and original byte preservation. Image cancellation after actual Inspect/Convert/Validate plus locked-temp cleanup failure is covered. Existing WPF layout/binding coverage now includes a populated Batch and stable translated choice.

The populated WPF layout test initially caught a read-only progress property used with ProgressBar's default two-way binding; explicit OneWay fixed it and the real populated view now opens. A terminal cancelled-job label was also corrected to avoid saying it is still waiting. A focused regression keeps individual engines disabled after uncertain batch cleanup even after failed rows are cleared. No existing tests or security expectations were removed.

## Actual Windows Release GUI acceptance

Observed through the real native application, file/folder dialogs and controls; no injected delays or security-setting changes. Disposable fixtures/results/screenshots are retained only under ignored `artifacts/phase-8-batch/`.

| Check | Actual observation |
|---|---|
| Multiple real images | PNG and JPEG inputs queued to PNG, both Completed. Independent WPF/WIC decoder read each final PNG (1×1). |
| Invalid then valid / pending cancel | Corrupt `bad.png` Failed; following `جيد جديد.png` Completed; third Pending job cancelled without output. Earlier successes retained. |
| Video | Five-minute `busy-video.mp4` (283,674,698 bytes) completed to MP4. Independent ffprobe: H.264 1280×720, AAC 44100 Hz, duration 300.333333 s. |
| Active Cancel Job | Video was Running when cancelled; queued MP3 then completed. Cancelled video had no final file or owned temp at that checkpoint. MP3 independently probed: 44100 Hz, 192000 bps, 300.218413 s. |
| Cancel Queue | Active video plus pending WAV both Cancelled; no new MP4/WAV or owned temp; previous MP3 retained; no ffmpeg/ffprobe process after cleanup. A later separate video job legitimately published an MP4 to this folder. |
| Close → No | Confirmation while Running at 39.436%, ffmpeg PID 17292 / app PID 32928 observed. No retained app/work; output subsequently verified and Completed. |
| Close → Yes | Twenty-minute `close-long.mp4` (1,134,693,683 bytes), separate `close-yes` destination. Confirmation at Running 50.3759%; ffmpeg PID 3264 / app PID 32928 and exact owned temp observed. Yes closed after cancellation; app/ffmpeg/ffprobe absent, destination empty, original hash unchanged. |
| Active theme/language | Dark switch observed while video Running 38.6166%. On longer fixture, Arabic → English plus navigation returned to same active queue at 36.994%; no reset/restart. Both idle themes/languages also viewed. |
| Document | Real Arabic/English `مستند تجريبي.md` → HTML Completed through native picker/Batch. Static UTF-8 inspection outside app confirmed original text and `dir="rtl" lang="ar"`; no HTML execution claimed. |
| Existing destination | Add refused an already-published MP3 destination; did not overwrite it. Clear completed/errors removed only rows, not existing results. |

Source SHA-256 before and after the relevant GUI operations (identical):

- PNG: `431CED6916A2A21A156E38701AFE55BBD7F88969FBBFC56D7FE099D47F265460`.
- JPEG: `42CBD31C7B87F87F6BCC6450181638F75FF6408C426E640C1264F11902C5F464`.
- Five-minute MP4: `184E4004831065F945F4D00FA2E61029D5443D17486A50DFC55AEC6E2D13DC8F`.
- Twenty-minute MP4: `0C4B66BC1D4E943FB9319ECB543076DD96D9F5F61F93E7BC314E8DB9C0DB52B8`.
- Markdown: `B4A306C43A687E5AACB4F951C7A2916FC14F50EFAE41D0F514D24FFEF9A41725`.

Actual screenshots: `01-queued-arabic-dark.jpg`, `02-images-result-arabic.jpg`, `03-failure-continues-and-pending-cancel.jpg`, `04-active-close-confirmation.jpg`, `05-light-queue-after-no.jpg`, `06-active-theme-switch.jpg`, `07-cancelled-video-audio-completed-english.jpg`, `08-cancel-entire-queue.jpg`, `09-language-state-preserved-idle.jpg`, `10-language-switch-active-long.jpg`, `11-active-close-yes-dialog.jpg`, `12-documents-html-completed.jpg`. Screenshot 03 precedes the cancelled-label-only fix; GUI evidence precedes the final cleanup-warning gating regression. Screenshot 09 is explicitly idle, not evidence for active localization; 10 is the active evidence.

## Remaining acceptance / limitations

Manual 125%/150% DPI remains owner-deferred to Phase 9. Automated WPF layout/binding checks cover minimum/default/large logical sizes in both languages, but are not physical DPI evidence or a complete keyboard/accessibility audit. GUI retry/error cleanup uncertainty is tested deterministically in xUnit, not reproduced with a deliberately unkillable native worker. No benchmarks claimed. Settings are reviewed fixed presets, not every individual panel's advanced settings. PDF/archive queue support, persistence and parallel workers are intentionally absent. The outstanding Phase 7 archive manual Active Cancel / Close No / Close Yes remain **NOT VERIFIED**; owner chose to skip them, and Phase 8 video-queue evidence does not silently complete them.

No Phase 9–12, Explorer/radial menu, installation, new dependency or merge is authorized. Stop for independent review and owner approval.
