#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:project ../src/OrbitConvert.App/OrbitConvert.App.csproj
// Phase 2D measurement helper (not part of the application or the test suite).
// Usage:
//   dotnet run tools/ImageBenchmark.cs -- gen <dir>
//   dotnet run tools/ImageBenchmark.cs -- run <source> <PNG|JPEG|WebP|BMP> <outDir> <warmRuns> [resizeWidth resizeHeight]
// Each "run" is meant to execute in a fresh process: the first conversion includes native initialization (cold),
// the following ones are steady state. Peak memory is the whole-process peak working set.
using System.Diagnostics;
using System.IO;
using ImageMagick;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

var mode = args.Length > 0 ? args[0] : "";
if (mode == "gen") { Generate(args[1]); return 0; }
if (mode == "run") { return await Run(args); }
Console.Error.WriteLine("Usage: gen <dir> | run <source> <format> <outDir> <warmRuns> [width height]");
return 2;

async Task<int> Run(string[] args)
{
    var source = args[1];
    var format = args[2];
    var outDir = args[3];
    var warmRuns = int.Parse(args[4]);
    var options = args.Length >= 7
        ? new ImageConversionOptions(ResizeEnabled: true, Width: int.Parse(args[5]), Height: int.Parse(args[6]))
        : new ImageConversionOptions();
    Directory.CreateDirectory(outDir);
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var startupPeak = process.PeakWorkingSet64;
    var service = new ImageConversionService(new MagickImageEngine());
    var extension = ImageConversionOptions.Extension(format);
    var stem = Path.GetFileName(source).Replace('.', '_') + "-to-" + format.ToLowerInvariant() + (options.ResizeEnabled ? $"-{options.Width}x{options.Height}" : "");
    var timings = new List<double>();
    long outputBytes = 0;
    for (var run = 0; run <= warmRuns; run++)
    {
        var destination = Path.Combine(outDir, $"{stem}-{run}{extension}");
        var clock = Stopwatch.StartNew();
        var result = await service.ConvertAsync(source, destination, format, options);
        clock.Stop();
        if (!result.IsSuccess) { Console.WriteLine($"FAILED {result.Error}"); return 1; }
        timings.Add(clock.Elapsed.TotalMilliseconds);
        outputBytes = new FileInfo(destination).Length;
        if (run > 0) { File.Delete(destination); } // Keep only the first output for inspection.
    }
    process.Refresh();
    var warm = timings.Skip(1).Order().ToArray();
    var median = warm.Length == 0 ? double.NaN : warm[warm.Length / 2];
    Console.WriteLine(string.Join('\t', Path.GetFileName(source), new FileInfo(source).Length, format,
        options.ResizeEnabled ? $"{options.Width}x{options.Height}" : "-", outputBytes,
        $"{timings[0]:F0}", $"{median:F0}", warm.Length == 0 ? "-" : $"{warm[0]:F0}-{warm[^1]:F0}",
        startupPeak / 1048576, process.PeakWorkingSet64 / 1048576));
    return 0;
}

// Synthetic, deterministic benchmark inputs (no external images).
void Generate(string dir)
{
    Directory.CreateDirectory(dir);
    using (var probe = new MemoryStream(new byte[] { 1 })) { new MagickImageEngine().Inspect(probe); } // Apply the app policy first.
    Save(Photo(1920, 1080), Path.Combine(dir, "photo-1080p.jpg"), MagickFormat.Jpeg, 90);
    Save(Photo(4000, 4000), Path.Combine(dir, "photo-16mp.jpg"), MagickFormat.Jpeg, 90);
    Save(Photo(1920, 1080), Path.Combine(dir, "photo-1080p.webp"), MagickFormat.WebP, 80);
    Save(Photo(1920, 1080), Path.Combine(dir, "photo-1080p.bmp"), MagickFormat.Bmp3, 0);
    Save(Screenshot(1920, 1080), Path.Combine(dir, "screenshot-1080p.png"), MagickFormat.Png, 0);
    Save(Transparent(1024, 1024), Path.Combine(dir, "transparent-1024.png"), MagickFormat.Png, 0);
}

void Save(MagickImage image, string path, MagickFormat format, uint quality)
{
    using (image)
    {
        if (quality > 0) { image.Quality = quality; }
        image.Depth = 8;
        if (format == MagickFormat.Bmp3) { image.Settings.Compression = CompressionMethod.NoCompression; }
        image.Write(path, format);
    }
}

MagickImage Photo(int width, int height)
{
    var random = new Random(42);
    var pixels = new byte[width * height * 3];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 3;
            var wave = Math.Sin(x / 37.0) * Math.Cos(y / 53.0);
            pixels[i] = Clamp(128 + 90 * wave + random.Next(-12, 13));
            pixels[i + 1] = Clamp(x * 255.0 / width + random.Next(-8, 9));
            pixels[i + 2] = Clamp(y * 255.0 / height + random.Next(-8, 9));
        }
    }
    return Image(width, height, pixels, "RGB");
}

MagickImage Screenshot(int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var panel = (x / 240 + y / 135) % 3;
            var text = (x % 9 < 6 && y % 18 < 11 && (x / 9 + y / 18) % 5 != 0) ? 60 : 0;
            pixels[i] = (byte)(panel == 0 ? 245 - text : panel == 1 ? 30 + text : 70);
            pixels[i + 1] = (byte)(panel == 0 ? 245 - text : panel == 1 ? 34 + text : 130);
            pixels[i + 2] = (byte)(panel == 0 ? 248 - text : panel == 1 ? 46 + text : 220);
            pixels[i + 3] = 255; // RGBA but fully opaque, like many real screenshots.
        }
    }
    return Image(width, height, pixels, "RGBA");
}

MagickImage Transparent(int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var distance = Math.Sqrt(Math.Pow(x - width / 2.0, 2) + Math.Pow(y - height / 2.0, 2)) / (width / 2.0);
            pixels[i] = 220; pixels[i + 1] = (byte)(x * 255 / width); pixels[i + 2] = 90;
            pixels[i + 3] = Clamp(255 * (1.2 - distance));
        }
    }
    return Image(width, height, pixels, "RGBA");
}

MagickImage Image(int width, int height, byte[] pixels, string map)
{
    var image = new MagickImage();
    image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, map));
    return image;
}

byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
