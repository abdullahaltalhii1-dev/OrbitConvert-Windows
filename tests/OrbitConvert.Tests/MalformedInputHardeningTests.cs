using System.Buffers.Binary;
using System.Diagnostics;
using ImageMagick;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.Tests;

// Deterministic, bounded corruption corpus: every mutation of every valid seed must either be rejected
// with a known error or convert to a fully validated output. No exception may escape the engine.
public sealed class MalformedInputHardeningTests
{
    private static readonly MagickImageEngine Engine = new();

    public static IEnumerable<object[]> Seeds => SeedNames.Select(name => new object[] { name });

    private static readonly string[] SeedNames =
    [
        "png-rgba", "png-rgb", "jpeg-baseline", "jpeg-progressive", "webp-lossy", "webp-lossy-alpha",
        "webp-lossless", "bmp-24", "bmp-32-bitfields", "bmp-8-palette", "bmp-top-down",
    ];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Seed_IsValidBeforeMutation(string name)
    {
        using var input = new MemoryStream(Seed(name));
        var result = Engine.Inspect(input);
        Assert.True(result.IsSuccess, $"{name}: {result.Error}");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Mutations_NeverThrowHangOrProduceUnvalidatedOutput(string name)
    {
        var seed = Seed(name);
        var anomalies = new List<string>();
        foreach (var (label, bytes) in Mutations(seed))
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var input = new MemoryStream(bytes);
                var inspected = Engine.Inspect(input);
                if (inspected.IsSuccess)
                {
                    foreach (var target in ImageConversionOptions.Formats)
                    {
                        using var source = new MemoryStream(bytes);
                        using var output = new MemoryStream();
                        var converted = Engine.Convert(source, output, target);
                        if (!converted.IsSuccess) { continue; }
                        output.Position = 0;
                        if (!Engine.ValidateOutput(output, converted.Inspection!).IsSuccess)
                        {
                            anomalies.Add($"{label}->{target}: converted output failed validation");
                        }
                    }
                }
            }
            catch (Exception error)
            {
                anomalies.Add($"{label}: {error.GetType().Name}: {error.Message}");
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) { anomalies.Add($"{label}: took {clock.Elapsed.TotalSeconds:F1}s"); }
        }
        Assert.True(anomalies.Count == 0, $"{name}: {anomalies.Count} anomalies\n" + string.Join("\n", anomalies.Take(40)));
    }

    internal static IEnumerable<(string Label, byte[] Bytes)> Mutations(byte[] seed)
    {
        // Truncation at the start, through the header, and across the payload.
        var cuts = Enumerable.Range(1, 64).Concat(Enumerable.Range(1, 24).Select(i => seed.Length * i / 25)).Distinct()
            .Where(cut => cut < seed.Length);
        foreach (var cut in cuts) { yield return ($"truncate@{cut}", seed[..cut]); }
        // Byte inversion: every header byte, then evenly spaced payload bytes.
        var flips = Enumerable.Range(0, Math.Min(seed.Length, 160))
            .Concat(Enumerable.Range(1, 48).Select(i => seed.Length * i / 49)).Distinct();
        foreach (var position in flips)
        {
            var copy = (byte[])seed.Clone();
            copy[position] ^= 0xFF;
            yield return ($"flip@{position}", copy);
        }
        // Length-field style damage: maximum value at each 4-byte header boundary.
        for (var position = 0; position + 4 <= Math.Min(seed.Length, 160); position += 4)
        {
            var copy = (byte[])seed.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(position), uint.MaxValue);
            yield return ($"max32@{position}", copy);
        }
        yield return ("trailing-garbage", seed.Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray());
        yield return ("trailing-copy", seed.Concat(seed).ToArray());
    }

    internal static byte[] Seed(string name) => name switch
    {
        "png-rgba" => ImageFixtures.Png(alpha: 128, width: 24, height: 3),
        "png-rgb" => Encode(ImageFixtures.PatternPng(), "PNG"),
        "jpeg-baseline" => Encode(ImageFixtures.PatternPng(), "JPEG"),
        "jpeg-progressive" => Magick(ImageFixtures.PatternPng(), image => { image.Settings.Interlace = Interlace.Plane; image.Format = MagickFormat.Jpeg; }),
        "webp-lossy" => Encode(ImageFixtures.PatternPng(), "WebP"),
        "webp-lossy-alpha" => Encode(ImageFixtures.Png(alpha: 128, width: 24, height: 3), "WebP"),
        "webp-lossless" => Magick(ImageFixtures.PatternPng(), image =>
        {
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
            image.Format = MagickFormat.WebP;
        }),
        "bmp-24" => Encode(ImageFixtures.PatternPng(), "BMP"),
        "bmp-32-bitfields" => Bmp(32, compression: 3, topDown: false),
        "bmp-8-palette" => Bmp(8, compression: 0, topDown: false),
        "bmp-top-down" => Bmp(24, compression: 0, topDown: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] Encode(byte[] source, string format)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        Assert.True(Engine.Convert(input, output, format).IsSuccess);
        return output.ToArray();
    }

    private static byte[] Magick(byte[] source, Action<MagickImage> configure)
    {
        using var seed = new MemoryStream(source);
        Assert.True(Engine.Inspect(seed).IsSuccess); // Applies the application's native policy first.
        using var image = new MagickImage(source);
        configure(image);
        return image.ToByteArray();
    }

    // Hand-built Windows BMPs for variants the BMP3 encoder never writes.
    internal static byte[] Bmp(ushort depth, uint compression, bool topDown, int width = 12, int height = 8)
    {
        var palette = depth == 8 ? 256 * 4 : 0;
        var masks = compression == 3 ? 12 : 0;
        var stride = (width * depth + 31) / 32 * 4;
        var pixelOffset = 14 + 40 + masks + palette;
        var data = new byte[pixelOffset + stride * height];
        "BM"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), (uint)pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), depth);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(30), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(34), (uint)(stride * height));
        if (compression == 3)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(54), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(58), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(62), 0x000000FF);
        }
        if (depth == 8)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(46), 256);
            for (var i = 0; i < 256; i++) { data[54 + i * 4] = (byte)i; data[55 + i * 4] = (byte)(255 - i); data[56 + i * 4] = (byte)(i / 2); }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < stride; x++) { data[pixelOffset + y * stride + x] = (byte)((x * 7 + y * 13) % 256); }
        }
        return data;
    }
}
