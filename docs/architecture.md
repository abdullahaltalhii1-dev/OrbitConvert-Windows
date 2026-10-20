# Architecture

OrbitConvert is a small .NET 10 WPF application using MVVM. The existing shell, themes, localization, native file picker, drag/drop, and file intake remain in place.

## Presentation

MainWindow composes UserControls and binds its title to MainViewModel.AppTitle. MainViewModel owns navigation, session theme/accent, localization, FileIntakeViewModel, and ImageConversionViewModel.

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

See image-engine-decision.md for exact resource settings and limitations. This phase has no batch workflow, color management, HEIC/AVIF or other engines, output thumbnails, telemetry, shell integration, or updater.
