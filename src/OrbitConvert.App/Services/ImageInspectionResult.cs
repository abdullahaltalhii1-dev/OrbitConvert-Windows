using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public enum ImageInspectionError
{
    None,
    EmptyInput,
    UnreadableInput,
    InvalidOrCorruptImage,
    UnsupportedFormat,
    TooLarge,
    UnsupportedBitDepth,
    UnsupportedColor,
    MultipleFrames,
    ResourceLimit,
    SamePath,
    DestinationExists,
    InvalidDestination,
    OutputFailure,
    OutputValidationFailed,
    InvalidSettings,
    Cancelled,
}

public sealed record ImageInspectionResult(ImageInspection? Inspection, ImageInspectionError Error)
{
    public bool IsSuccess => Inspection is not null && Error == ImageInspectionError.None;

    public static ImageInspectionResult Success(ImageInspection inspection) =>
        new(inspection, ImageInspectionError.None);

    public static ImageInspectionResult Failure(ImageInspectionError error) =>
        new(null, error);
}
