using System.IO;
using System.Text;

namespace OrbitConvert.App.Services.Archives;

internal sealed class ArchivePaths
{
    private readonly Dictionary<string, bool> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _spellings = new(StringComparer.OrdinalIgnoreCase);
    public static string Validate(string name, bool directory = false)
    {
        if (name.Length == 0 || name.Length > ArchiveLimits.RelativePath || name[0] is '/' or '\\') { throw new ArchiveRejected(ArchiveError.UnsafePath); }
        var normalized = name.Replace('\\', '/');
        if (directory && normalized.EndsWith('/')) { normalized = normalized[..^1]; }
        var components = normalized.Split('/');
        if (components.Length > ArchiveLimits.Depth) { throw new ArchiveRejected(ArchiveError.TooLarge); }
        foreach (var part in components)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                part.Any(c => char.IsControl(c) || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
            { throw new ArchiveRejected(ArchiveError.UnsafePath); }
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(stem[3])) { throw new ArchiveRejected(ArchiveError.UnsafePath); }
        }
        try { _ = new UTF8Encoding(false, true).GetByteCount(normalized); }
        catch (EncoderFallbackException) { throw new ArchiveRejected(ArchiveError.UnsafePath); }
        return normalized;
    }
    public string Add(string name, bool directory)
    {
        var path = Validate(name, directory);
        var key = path.Normalize(NormalizationForm.FormC);
        if (!_explicit.Add(key)) { throw new ArchiveRejected(ArchiveError.DuplicateEntry); }
        var pieces = key.Split('/');
        var original = path.Split('/');
        for (var i = 1; i <= pieces.Length; i++)
        {
            var componentKey = string.Join('/', pieces.Take(i)); var spelling = string.Join('/', original.Take(i));
            if (_spellings.TryGetValue(componentKey, out var prior) && !prior.Equals(spelling, StringComparison.OrdinalIgnoreCase)) { throw new ArchiveRejected(ArchiveError.DuplicateEntry); }
            _spellings[componentKey] = spelling;
        }
        for (var i = 1; i < pieces.Length; i++)
        {
            var parent = string.Join('/', pieces.Take(i));
            if (_paths.TryGetValue(parent, out var isDirectory) && !isDirectory) { throw new ArchiveRejected(ArchiveError.DuplicateEntry); }
            _paths[parent] = true;
        }
        if (_paths.TryGetValue(key, out var existing) && (!existing || !directory)) { throw new ArchiveRejected(ArchiveError.DuplicateEntry); }
        _paths[key] = directory;
        if (_paths.Count > ArchiveLimits.Entries) { throw new ArchiveRejected(ArchiveError.TooLarge); }
        return path;
    }
    public static string Under(string root, string relative)
    {
        var validated = Validate(relative);
        var result = Path.GetFullPath(Path.Combine(root, validated.Replace('/', Path.DirectorySeparatorChar)));
        if (result.Length > ArchiveLimits.FullPath || !result.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        { throw new ArchiveRejected(ArchiveError.UnsafePath); }
        return result;
    }
}
