using System.IO;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public interface IImageEngine
{
    ImageInspectionResult Inspect(Stream input);
}
