using System.Diagnostics;
using System.Globalization;

namespace OrbitConvert.App.Services.Media;

internal static class FfmpegProgress
{
    internal static Action<string> Create(TimeSpan? total, Stopwatch clock, Action<TimeSpan, double?, TimeSpan> report)
    {
        var processed = TimeSpan.Zero;
        return line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) &&
                microseconds is >= 0 and <= long.MaxValue / 10)
            {
                processed = TimeSpan.FromTicks(microseconds * 10);
            }
            else if (line.StartsWith("progress=", StringComparison.Ordinal))
            {
                double? fraction = total is { TotalSeconds: > 0 } duration
                    ? line == "progress=end" ? 1.0 : Math.Clamp(processed.TotalSeconds / duration.TotalSeconds, 0, 0.999) : null;
                report(processed, fraction, clock.Elapsed);
            }
        };
    }
}
