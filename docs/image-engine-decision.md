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

## Integration boundary

`IImageEngine` currently exposes inspection of a caller-provided readable stream. `MagickImageEngine` decodes the image, returns only format, dimensions, bit depth, and alpha presence, and converts expected corrupt/unreadable input failures into explicit result values. It does not accept output paths, create files, convert formats, or connect to the WPF UI.

Resource limits for untrusted or very large images, background execution, file-path handling, output validation, and conversion policy remain required future work. They must be addressed before this adapter is connected to user-selected files in Phase 2B.

## References

- https://github.com/dlemstra/Magick.NET/blob/main/docs/Readme.md
- https://github.com/dlemstra/Magick.NET/releases/tag/15.0.0
- https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU/15.0.0
- https://www.nuget.org/packages/Magick.NET-Q16-AnyCPU/15.0.0
