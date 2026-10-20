# Phase 2C validation

## Scope delivered

- Input and output: PNG, JPEG, WebP, BMP. Any supported source can target any supported format, including the same format (useful with resize).
- Quality 1–100 for JPEG and WebP only (default 90). PNG and BMP show no quality control because they are lossless.
- Optional resize. Aspect ratio is preserved by default (fit inside width × height, no cropping). Turning it off requests exact dimensions and may stretch; the UI says so and shows the expected output size before conversion.
- Transparency is preserved for PNG/WebP and composited onto white (#FFFFFF) for JPEG/BMP. A fully opaque alpha channel is dropped before encoding so output validation matches what encoders actually write.
- EXIF orientation is applied to pixels before resizing, then EXIF is removed.
- One image per operation. No batch, HEIC/AVIF, or other engines.

## Format and safety rules

- Native policy allows only PNG/JPEG/WebP read+write, BMP read, and BMP3 write. Delegates, filters, and all other coders stay denied.
- WebP container guard: RIFF size must match, chunks must be in bounds and padded, exactly one VP8/VP8L bitstream, VP8X canvas must match the bitstream. Animation (ANIM/ANMF/VP8X flag) is rejected as multi-frame; ICCP or the VP8X ICC flag is rejected as unsupported color.
- BMP container guard: file size must match, header must be 12/40/108/124 bytes, compression must be none or bitfields, bitfield masks must not overlap or exceed 8 bits per channel, V4/V5 color space must be sRGB/Windows (no embedded or linked profiles), and the pixel array must fit inside the file. Embedded JPEG/PNG BMPs and RLE are rejected.
- Any embedded ICC/ICM profile is rejected after decoding, for every format.
- The PR #7 protection is kept and widened: corrupt-image and coder warnings during ping, read, or write reject the operation.
- Existing limits are unchanged: 25 MiB input, 8192 px per side, 16 million pixels; the resize target must also stay within those limits. Output validation still decodes the whole file and checks format, dimensions, depth, and alpha.
- All Phase 2B file-safety behavior is unchanged: read-only source, exclusive temporary file, full validation, no-overwrite move, and cleanup of owned temporary files only. The destination extension must match the chosen format.

## Automated coverage

`ExtendedImageConversionTests` covers all 16 source/target pairs with Arabic and mixed-script filenames and original-byte preservation, and decodes non-WebP outputs independently with WPF/WIC (WIC has no guaranteed WebP codec, so WebP outputs are checked structurally and by the engine). It also covers:

- Quality changing both bytes and pixel error.
- Resize with and without aspect ratio.
- Transparency preserved or composited.
- Orientation before resize.
- Invalid settings creating no file.
- Malformed, animated, ICC, and oversized WebP/BMP headers.
- A real animated WebP.
- High-precision BMP masks.
- Existing destinations and wrong extensions.
- Fully opaque RGBA sources for every target.

View-model tests cover option state, format switching, and validation messages.

## Local command results (2026-10-20)

- `dotnet restore`: passed.
- `dotnet build -c Release`: passed, zero warnings and errors.
- `dotnet test -c Release`: 114 passed, 0 failed, 0 skipped.
- `dotnet format --verify-no-changes`: passed.

## Windows GUI smoke checks

Screenshots taken from the running WPF app:

- [Arabic, dark: PNG → WebP with quality 73 and resize 48 × 48 (expected 48 × 32)](screenshots/16-phase-2c-arabic-resize.jpg)
- [Arabic, dark: the same WebP conversion saved and verified](screenshots/17-phase-2c-webp-success.jpg)
- [English, light: PNG → BMP with resize, saved and verified](screenshots/18-phase-2c-english-light.jpg)

These screenshots show RTL/LTR layout, the quality control appearing only for lossy targets, the expected size preview, and Arabic folder paths.

## Unverified and residual limitations

- The GUI was not re-run after the opaque-alpha fix; that fix is covered by automated tests only.
- Treating every native coder warning as a failure is conservative and may reject some technically recoverable real-world files.
- Not checked: 125%/150% DPI, a full screen-reader pass, a broad malicious-image corpus, peak-memory measurements, disk-full behavior, and forced-crash recovery.
- Color management is limited to unprofiled sRGB/RGB/grayscale. ICC, CMYK, and sources above 8 bits remain rejected. Metadata other than orientation is not preserved by design.
- In-process native limits are not a sandbox or a hard cancellation deadline.
