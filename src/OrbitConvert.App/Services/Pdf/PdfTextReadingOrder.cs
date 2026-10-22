using System.Runtime.InteropServices;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OrbitConvert.App.Services.Pdf;

/// <summary>Horizontal Arabic extraction from PdfPig glyph geometry, not reversal of a Unicode string.
/// ICU's inverse bidi map reorders whole glyph replacements, preserving ligatures and combining marks.
/// Complex columns, rotated text and ambiguous PDF Unicode mappings still require review.</summary>
internal static class PdfTextReadingOrder
{
    private readonly record struct Glyph(string Text, double X, double Y, double EndX, double Size);

    public static string Extract(Page page)
    {
        var letters = page.Letters;
        if (letters.Count > 200_000) { throw new PdfRejected(PdfError.TooLarge); }
        if (!letters.Any(x => HasArabic(x.Value)) || letters.Any(x => Math.Abs(x.StartBaseLine.Y - x.EndBaseLine.Y) > 0.01))
        { return ContentOrderTextExtractor.GetText(page); }
        return ExtractHorizontal(letters);
    }

    internal static string ExtractHorizontal(IReadOnlyList<Letter> letters)
    {
        if (letters.Count > 200_000 || letters.Sum(x => (long)x.Value.Length) > PdfLimits.TextCharacters) { throw new PdfRejected(PdfError.TooLarge); }
        var glyphs = new List<Glyph>();
        for (var i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            if (letter.Value.Length == 0) { continue; }
            var anchor = letter;
            // PdfPig puts ActualText on the first glyph of a marked span. A zero-advance mark can
            // carry the entire base+mark replacement; the following empty base supplies its baseline.
            if (Math.Abs(letter.EndBaseLine.X - letter.StartBaseLine.X) < 0.01 && letter.Value.Length > 1 &&
                i + 1 < letters.Count && letters[i + 1].Value.Length == 0 &&
                Math.Abs(letters[i + 1].StartBaseLine.X - letter.StartBaseLine.X) < Math.Max(1, letter.PointSize))
            { anchor = letters[i + 1]; }
            if (!double.IsFinite(anchor.StartBaseLine.X) || !double.IsFinite(anchor.StartBaseLine.Y) ||
                !double.IsFinite(anchor.EndBaseLine.X) || !double.IsFinite(anchor.PointSize)) { throw new PdfRejected(PdfError.InvalidPdf); }
            glyphs.Add(new(letter.Value, anchor.StartBaseLine.X, anchor.StartBaseLine.Y, anchor.EndBaseLine.X, Math.Max(1, anchor.PointSize)));
        }
        var lines = new List<List<Glyph>>();
        foreach (var glyph in glyphs.OrderByDescending(x => x.Y))
        {
            if (lines.Count == 0 || Math.Abs(lines[^1][0].Y - glyph.Y) > Math.Min(2, glyph.Size * 0.15)) { lines.Add([]); }
            lines[^1].Add(glyph);
        }
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(x => x.X).ToArray();
            var cells = new List<List<string>> { new() };
            Glyph? previous = null;
            foreach (var glyph in ordered)
            {
                if (previous is { } last)
                {
                    var gap = glyph.X - last.EndX;
                    if (gap > Math.Max(last.Size, glyph.Size) * 3) { cells.Add([]); }
                    else if (gap > Math.Min(last.Size, glyph.Size) * 0.2 && !char.IsWhiteSpace(last.Text[^1]) && !char.IsWhiteSpace(glyph.Text[0])) { cells[^1].Add(" "); }
                }
                cells[^1].Add(glyph.Text); previous = glyph;
            }
            // Infer only a line/cell base direction, never the interface language. Latin-first lines
            // retain LTR flow; large table gaps remain explicit cell separators, not joined words.
            if (IsRtl(ordered.Select(x => x.Text))) { cells.Reverse(); }
            var text = string.Join('\t', cells.Select(cell => HasArabic(string.Concat(cell)) ? LogicalGlyphs(cell) : string.Concat(cell)));
            if ((long)result.Length + text.Length + 2 > PdfLimits.TextCharacters) { throw new PdfRejected(PdfError.TooLarge); }
            result.AppendLine(text.Trim());
        }
        return result.ToString().TrimEnd();
    }

    private static bool HasArabic(string value) => value.Any(c => char.IsLetter(c) && c is >= '\u0600' and <= '\u06ff' or >= '\u0750' and <= '\u077f' or >= '\u08a0' and <= '\u08ff' or >= '\ufb50' and <= '\ufdff' or >= '\ufe70' and <= '\ufeff');
    private static bool IsRtl(IEnumerable<string> visual)
    {
        var strong = visual.LastOrDefault(text => text.Any(char.IsLetter));
        return strong is not null && HasArabic(strong);
    }

    private static string LogicalGlyphs(List<string> glyphs)
    {
        // One bidi representative per atomic glyph replacement. Mapping the original values avoids
        // splitting ActualText ligatures, combining sequences or surrogate pairs. No NFKC on Latin.
        var representatives = new string(glyphs.Select(text => char.IsSurrogate(text[0]) ? '\ufffc' : text[0]).ToArray());
        var error = 0;
        var bidi = Native.ubidi_openSized(representatives.Length, 0, ref error);
        if (error > 0 || bidi == IntPtr.Zero) { if (bidi != IntPtr.Zero) { Native.ubidi_close(bidi); } throw new PdfRejected(PdfError.TooLarge); }
        var source = IntPtr.Zero;
        try
        {
            source = Marshal.StringToHGlobalUni(representatives);
            Native.ubidi_setReorderingMode(bidi, 5); // UBIDI_REORDER_INVERSE_LIKE_DIRECT; Windows ICU C API.
            Native.ubidi_setPara(bidi, source, representatives.Length, IsRtl(glyphs) ? (byte)1 : (byte)0, IntPtr.Zero, ref error);
            if (error > 0) { throw new PdfRejected(PdfError.InvalidPdf); }
            var map = new int[glyphs.Count]; Native.ubidi_getVisualMap(bidi, map, ref error);
            if (error > 0) { throw new PdfRejected(PdfError.InvalidPdf); }
            var seen = new bool[map.Length]; var output = new StringBuilder();
            foreach (var index in map)
            {
                if ((uint)index >= map.Length || seen[index]) { throw new PdfRejected(PdfError.InvalidPdf); }
                seen[index] = true;
                var value = glyphs[index];
                output.Append(HasArabic(value) ? value.Normalize(NormalizationForm.FormKC) : value);
                if (output.Length > PdfLimits.TextCharacters) { throw new PdfRejected(PdfError.TooLarge); }
            }
            return output.ToString();
        }
        finally { Native.ubidi_close(bidi); if (source != IntPtr.Zero) { Marshal.FreeHGlobal(source); } }
    }

    private static class Native
    {
        // Windows 11 supplies ICU. Resolve only the OS DLL, never a user/input-adjacent library.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr ubidi_openSized(int length, int runs, ref int error);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ubidi_close(IntPtr bidi);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ubidi_setReorderingMode(IntPtr bidi, int mode);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ubidi_setPara(IntPtr bidi, IntPtr text, int length, byte level, IntPtr levels, ref int error);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ubidi_getVisualMap(IntPtr bidi, [Out] int[] map, ref int error);
    }
}
