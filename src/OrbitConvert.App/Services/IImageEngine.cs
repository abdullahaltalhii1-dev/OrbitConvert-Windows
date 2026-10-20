using System.IO;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public interface IImageEngine
{
    ImageInspectionResult Inspect(Stream input);

    ImageInspectionResult Convert(Stream input, Stream output, string outputFormat, ImageConversionOptions? options = null);

    ImageInspectionResult ValidateOutput(Stream output, ImageInspection expected);
}
