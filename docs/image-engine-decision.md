# Phase 2A image engine decision

## Selected package

- Package: `Magick.NET-Q8-AnyCPU`
- Pinned version: `15.0.0`
- Upstream engine: ImageMagick 7.1.2-33
- License reported by the package: Apache-2.0

The AnyCPU package preserves the application's current architecture-neutral build. Version 15.0.0 directly targets .NET 10 and was the current stable release when this checkpoint was implemented.

## Q8 versus Q16 assessment

Q8 stores 8 bits per color component. Q16 stores 16 bits per component and preserves higher-precision source data, but the Magick.NET documentation states that Q16 uses twice the resources of Q8. The first planned OrbitConvert workflow is ordinary PNG/JPEG inspection and, in a later checkpoint, 8-bit conversion. Q8 is therefore the smaller-resource baseline unless a measured 16-bit quality requirement is introduced.

The Q8 and Q16 AnyCPU NuGet packages are both approximately 84 MB compressed at version 15.0.0, because each package bundles native binaries for multiple architectures. A platform-specific package could reduce the deployed size, but selecting one now would prematurely remove architecture support.

No Q8/Q16 runtime benchmark was performed in this checkpoint. Loading both quantum variants into one application is not a representative production configuration, and building separate benchmark applications would expand this deliberately small integration task. Before OrbitConvert claims 16-bit preservation, measure Q8 and Q16 in isolated, equivalent processes using 8-bit and 16-bit PNG fixtures and record elapsed time, peak working set, decoded pixel values, and output fidelity.

## Phase 2B integration and protection

The package remains pinned to 15.0.0; no new NuGet dependency was added.

- Input limits: 25 MiB encoded bytes, 8192 pixels per side, 16,000,000 total pixels.
- The stream is read into a bounded snapshot; source size and dimensions are checked before full decoding.
- PNG IHDR bit depth is read directly from container bytes. A valid synthetic 16-bit PNG is independently decoded by WPF as 64-bit RGBA and rejected before Magick.NET reads it. Q8-reported decoded depth is not used to detect source precision.
- Only explicit PNG/JPEG coders are used. The local ImageMagick/policy.xml denies all other coders, delegates, and filters.
- Native policy: 256 MiB pixel-cache memory, zero mapped/disk cache, width/height 8192, list-length 8, at most 2 threads, and native time limit 30 seconds. The installed native build reports 1 thread on the development host. List-length 8 allows internal images; business validation still rejects more than one source frame.
- Verified 15.0.0 properties additionally set maximum single native allocation to 128 MiB and maximum profile size to 1 MiB.
- Magick.NET requires the complete bundled configuration. ConfigurationFiles.Default is initialized with the app policy; its generated temporary configuration directory is deleted on normal process exit. No machine-wide configuration is changed.
- Output verification allows up to 96 MiB encoded bytes to accommodate a PNG larger than its JPEG source, while retaining dimension/pixel guards.
- The native policy is process-global; operations are serialized. Its time setting is not an OS-enforced kill deadline or managed cancellation guarantee. These are provisional safety limits, not a benchmark or a sandbox. Total process memory includes managed snapshots and native allocations beyond pixel-cache memory.
- In-process native failures remain a residual risk. Forced process termination can leave owned temporary output/configuration files; the app does not sweep folders or delete files from earlier operations.

## Supported quality and color boundary

PNG → JPEG uses quality 90 and composites full/partial transparency onto white (#FFFFFF). JPEG → PNG forces 8 bits per component. Unsupported directions are rejected.

PNG sources above 8 bits, APNG, MPO/multiple frames, concatenated JPEGs, CMYK, ICC profiles, PNG cICP, and nonstandard gAMA/cHRM are rejected. Unprofiled sRGB/RGB/grayscale and standard sRGB PNG information are accepted. The native decoder validates remaining structure and data; the container guard is not a replacement decoder.

EXIF orientation is normalized into pixels and EXIF removed afterward. Other supported native color information is retained; this is not a general metadata/color-management feature. JPEG decoding cannot restore lost detail. JPEG files with trailing data are conservatively rejected. Other formats, quality controls, resize, and profile management remain deferred.

The UI uses a focused ImageConversionViewModel and ImageConversionService. The service owns read-only source access, exclusive temporary creation, validation, no-overwrite finalization, and owned-file cleanup. No output is reported as successful before those steps finish.

## Phase 2C additions

- The package remains pinned to 15.0.0; no new NuGet dependency was added.
- Policy coders: PNG, JPEG, and WebP read+write; BMP read; BMP3 write. BMP3 (Windows 3 header) is written for broad compatibility. All other coders, delegates, and filters stay denied.
- WebP is encoded lossy at the selected quality with alpha quality 100. JPEG uses the selected quality. PNG and BMP are lossless and take no quality setting.
- Resize happens after orientation normalization, within the same 8192-per-side and 16 MP limits.
- Native corrupt-image and coder warnings during ping, read, or write reject the operation.
- Embedded ICC/ICM profiles are rejected for every format. This is still not a color-managed or metadata-preserving pipeline.

See phase-2c-validation.md for container rules and evidence.

## Phase 2D hardening

- BMP output is written uncompressed (`CompressionMethod.NoCompression`). BMP3 otherwise writes palette and grayscale images as RLE8, which the input guard rejects, so palette or grayscale sources could not be converted to BMP.
- The bounded input and output snapshot reads seekable streams into one exact-size buffer. This lowered the measured peak working set for a 16 MP BMP output from 478 MiB to 282 MiB, and for a 16 MP PNG output from 421 MiB to 193 MiB.
- The limits are unchanged and are now tested at their exact boundaries. A deterministic corruption corpus of about 3,600 mutated inputs confirms that no exception escapes the engine.
- ImageMagick's `Progress` cancellation is honored only by the WebP encoder. JPEG decoding, resizing, and PNG encoding ignore it, so cooperative cancellation is not implemented.

See phase-2d-validation.md for measurements and evidence.

## References

- https://github.com/dlemstra/Magick.NET/blob/main/docs/Readme.md
- https://github.com/dlemstra/Magick.NET/releases/tag/15.0.0
- https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU/15.0.0
- https://www.nuget.org/packages/Magick.NET-Q16-AnyCPU/15.0.0
