# Architecture

OrbitConvert is a small .NET 10 WPF application using MVVM. The existing shell, themes, localization, native file picker, drag/drop, and file intake remain in place.

## Presentation

MainWindow composes UserControls and binds its title to MainViewModel.AppTitle. MainViewModel owns navigation, session theme/accent, localization, FileIntakeViewModel, ImageConversionViewModel, and VideoConversionViewModel.

FileIntakeViewModel retains the observable collection, case-insensitive duplicate prevention, metadata-only intake, remove/clear actions, and localized statuses. Intake does not automatically decode or convert files.

ImagesView replaces the fictional ImagesPreviewView. Its selector binds to FileIntake.SelectedFiles and chooses one item. Inspecting is explicit. ImageConversionViewModel owns inspected metadata, the selected output format, quality, resize options (parsed and validated into an ImageConversionOptions record), the expected output size, destination, busy state, localized status keys, and completed output path. The view contains only SaveFileDialog handling; the dialog's write-test file is disabled (CreateTestFile = false) because the service verifies writability with its own temporary file. AsyncCommand prevents repeated execution; the view model prevents conflicting actions. MainWindow rejects normal closing while an image operation is busy. Settings and navigation remain usable.

Paths and format identifiers use LTR bindings even in Arabic. LocalizationService still resolves RESX keys, falls back to English, and refreshes messages on language changes. Only the language preference is persisted.

## Processing and filesystem boundaries

IImageEngine exposes Inspect, Convert, and ValidateOutput using streams. MagickImageEngine contains all Magick.NET operations and has no output-path API. ImageInputGuard bounds stream bytes (seekable streams are read into one exact-size buffer; a stream that grows during the read is rejected), reads PNG IHDR bit depth before Q8 decoding, validates dimensions, scans container structure, rejects animation/MPO/concatenated JPEGs and unsupported color profiles, and forces the matching PNG/JPEG/WebP/BMP reader. WebP RIFF chunks and BMP headers are checked for bounds, animation, profiles, compression, and mask precision before native decoding. Native ping precedes full pixel decoding.

MagickImageEngine applies its process-local native policy once, with a lock around native operations. It normalizes EXIF orientation, removes EXIF to avoid double rotation, rejects embedded ICC profiles, resizes when requested, drops fully opaque alpha, composites alpha onto white for JPEG/BMP, applies JPEG/WebP quality, encodes 8-bit PNG/JPEG/WebP/BMP3, and returns explicit metadata/errors. ValidateOutput fully decodes and checks actual format, dimensions, depth, and alpha against the expected result.

ImageConversionService owns source/destination paths. All inspection/conversion work runs on Task.Run behind a shared one-operation semaphore. It opens the source read-only with FileShare.Read, rejects identical/existing destinations, and creates a unique .orbitconvert-{guid}.tmp file in the destination directory with FileMode.CreateNew. Ownership begins only after creation succeeds.

After encoding, it flushes/closes the temporary file, reopens it for full validation, and finalizes with File.Move(overwrite: false). A destination created during the operation is preserved. Expected failures return explicit errors; finally deletes only the owned temporary path. Cleanup failures are reported separately. No directory sweeping or original-file writes occur.

## Tests and limits

xUnit tests cover native conversion, container guards, file safety, finalization races, cleanup failures, view-model async state, localization, and existing intake/shell behavior. WPF/WIC independently decodes output in dedicated STA test threads. Synthetic PNG fixtures use valid headers, zlib data, and CRCs, including a real 16-bit PNG verified independently as 64 bits per RGBA pixel.

See image-engine-decision.md for exact image resource settings and limitations. There is no batch workflow, color management, HEIC/AVIF, output thumbnails, telemetry, shell integration, or updater.

## Video / external-process boundary (Phase 3)

VideosView shares the intake collection but selects its own single source. VideoConversionViewModel owns explicit inspection, metadata, quality, maximum height, destination, localized status, progress, cancellation, and the task that signals completed cleanup. No process starts merely by selecting a file. Its Progress<T> captures the WPF synchronization context and rejects stale queued reports. MainWindow offers cancel-and-wait or keep-waiting when a video operation is busy. Image and video panels disable conflicting operations while the other engine is busy; navigation, theme, and language remain available.

VideosView code-behind only hosts SaveFileDialog (CreateTestFile=false, MP4 only). The existing native picker and drop area remain unchanged. Both languages have complete video keys; paths/codecs use literal identifiers and LTR where necessary. Documents remain Coming Soon.

Services/Media contains ProcessRunner and FfmpegLocator. ProcessRunner uses ProcessStartInfo.ArgumentList, no shell, bounded captured output, asynchronous pipes, startup/runtime/inactivity timeouts, process-tree termination and a Windows kill-on-close job object. Job creation/configuration failure prevents process startup. Assignment checks the native boolean result and captures its Win32 error: if the child is still running, failure immediately enters the existing bounded termination path and returns StartFailed (never success). A failed assignment is harmless only after the child's exit is observed. StartFailed maps to the existing ToolsUnavailable video error; unconfirmed termination still maps to ProcessTerminationFailed/cleanup warning. Kill-on-close protection begins after confirmed assignment, not atomically with Process.Start. Stops have bounded grace periods; a process-creation call that blocks cannot be forcibly aborted by managed code, so a continuation attempts to kill a late-started process. This exceptional case can leave temporary files and is not represented as confirmed cleanup.

Services/Video contains immutable options/results, FFprobe JSON parsing and limits, FFmpeg argument construction, and VideoConversionService. It holds the source read-only, serializes video operations, restricts protocols and demuxers, reserves one owned temporary file, runs H.264/AAC encoding, probes and fully decodes the candidate, and moves with overwrite:false only after validation. Cancellation is checked again before finalization. It deletes only its own temporary file and reports cleanup failures. No engine binary is downloaded at runtime.

Real process/integration tests exercise FFmpeg, including codec/container fixtures, quality/SSIM, rotation, streams, progress, cancellation, finalization races, corruption, and Unicode paths. View-model tests separately cover presentation and cancellation waiting; an STA WPF layout/binding test loads actual resources and all sections in both languages at minimum/default/large sizes. See phase-3-video-validation.md for measured results and boundaries; this is not an OS media sandbox.

## Audio (Phase 4)

AudiosView and AudioConversionViewModel follow the existing single-source MVVM pattern, sharing FileIntake.SelectedFiles without automatic processing. They expose actual audio metadata, MP3 bitrate, format-specific WAV/FLAC policies, sample-rate selection, destination, real progress, cancellation and bilingual status. MainViewModel observes all three busy states to disable conflicting image/video/audio panels. MainWindow's existing close handler now also waits for audio cancellation/cleanup; code-behind remains limited to native dialogs and window behavior.

AudioConversionService reuses FfmpegLocator and the unchanged fail-closed ProcessRunner. Capability detection includes audio encoders independently of video support. AudioProbeParser validates actual containers/streams, chooses the default audio track (else first), rejects surround/unsupported codecs, and records unknown metadata honestly. AudioArguments maps only that stream and removes source metadata/other streams. Output must match its container, codec, channels, rate, precision/MP3 bitrate and duration, then pass a full strict decode before no-overwrite finalization. Source handles remain read-only; cleanup is operation-owned.

Audio and video share MediaOperationGate (a semaphore, not a batch queue) and FfmpegProgress's line parser. Existing video options/results/API remain unchanged. No new process runner, engine acquisition or package was introduced. See phase-4-audio-validation.md for exact support, PCM fidelity, limits and GUI verification status.
