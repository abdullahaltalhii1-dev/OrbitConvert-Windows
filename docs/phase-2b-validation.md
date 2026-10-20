# Phase 2B validation

## Automated coverage

The suite exercises PNG to JPEG and JPEG to PNG with real generated images. WPF/WIC decodes the results independently. Tests cover white alpha compositing, real 16-bit PNG rejection, source byte preservation, Arabic/mixed-language names, existing destinations, identical paths, finalization races, output corruption, partial writes, cleanup failures, inaccessible sources/directories, encoded-size/dimension limits, unsupported formats/profiles/animation, concatenated JPEGs, orientation, native policy enforcement, asynchronous duplicate prevention, and live message translation.

The old fictional output-preview test was removed; shell, file intake, language, and stream-error regression tests remain. No Q8/Q16 performance benchmark is claimed.

## Windows GUI smoke checks

On the Windows development host, the actual WPF application opened and closed normally. The native picker added an image from an Arabic directory with an Arabic/mixed-language filename. Images inspection showed actual format/dimensions and selected the opposite output format. SaveFileDialog supplied a new destination; real PNG to JPEG and JPEG to PNG operations both reported verified success. Both output files opened in Windows Paint without decoder errors.

Arabic and English runtime switching and both Light and Dark themes were exercised. The existing success message updated when the language changed. The intake list retained both files while the Images selector operated on one file only. A low-contrast default ComboBox was replaced with a theme-aware template and checked again before delivery.

Generated smoke images remain under ignored artifacts/phase-2b-smoke/. Source files are not user documents. Automated tests use isolated temporary directories.

The final build also rejected an existing JPEG destination through the UI, then successfully converted to a fresh filename. Default and maximized layouts, scrolling, keyboard selection, the corrected filename display, and theme contrast were checked. Actual screenshots of the final saved-and-verified state:

- screenshots/12-phase-2b-english-dark.png
- screenshots/13-phase-2b-english-light.png
- screenshots/14-phase-2b-arabic-light.png
- screenshots/15-phase-2b-arabic-dark.png

## Local command results

- dotnet restore: passed.
- dotnet build -c Release: passed, zero warnings/errors.
- dotnet test -c Release: 59 passed, zero failed/skipped.
- dotnet format --verify-no-changes: passed.
- NuGet vulnerability audit including transitive packages: no known vulnerable packages reported by the current configured source.

## Unverified and residual limitations

- No 125%/150% DPI or exhaustive accessibility/screen-reader pass was performed.
- No exhaustive malicious-image corpus, peak-memory benchmark, disk-full simulation, or forced-process-crash recovery test was performed.
- The busy-state/reentry checks use a controlled delayed service in unit tests; small real smoke images finish too quickly for prolonged responsiveness measurement.
- WPF binding trace output was not separately collected during the manual smoke check.
- Resource controls are in-process native controls, not complete isolation. Abnormal termination can leave operation-owned temporary files.
- ICC/CMYK, PNG above 8-bit, animation, and all formats except PNG/JPEG are intentionally unavailable.
