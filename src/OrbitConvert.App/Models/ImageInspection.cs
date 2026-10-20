namespace OrbitConvert.App.Models;

public sealed record ImageInspection(
    uint Width,
    uint Height,
    uint BitDepth,
    string Format,
    bool HasAlpha);
