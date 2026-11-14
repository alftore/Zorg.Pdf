using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Pdf.Fonts;

namespace Zorg.Pdf.Tests.Fonts;

public sealed partial class KerningTests
{
    private const string Sample = "AVATAR To Wa Ty LT Yo P. F, VA";

    [Fact]
    public void ClassicPairsKernTighter()
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        Assert.NotEqual("none", font.KerningSource);

        Assert.True(Kern(font, 'A', 'V') < 0, "A V should kern.");
        Assert.True(Kern(font, 'T', 'o') < 0, "T o should kern.");
        Assert.Equal(0, Kern(font, 'l', 'l'));
    }

    [Fact]
    public void GposAgreesWithTheOldKernTableOnThePairsItHas()
    {
        TrueTypeFont font = TrueTypeFont.Parse(TestFonts.Require());
        if (font.KerningSource != "GPOS" || !font.HasTable("kern"))
        {
            Assert.Skip("The test font doesn't have both kerning tables.");
        }

        int compared = 0;
        int nonZero = 0;
        foreach (char left in "AFLPTVWYafkortvwy.,")
        {
            foreach (char right in "AJTVWYacdeoqsuvwy.,")
            {
                ushort l = font.GlyphFor(left);
                ushort r = font.GlyphFor(right);
                // GPOS usually has more pairs than the old table (Arial kerns "AT" only in GPOS), so only the
                // pairs the old table has are compared.
                int old = font.KernTableKerning(l, r);
                if (old != 0)
                {
                    Assert.True(font.GposKerning(l, r) == old, $"'{left}{right}': GPOS {font.GposKerning(l, r)}, kern {old}");
                    nonZero++;
                }

                compared++;
            }
        }

        Assert.True(nonZero > 10, $"Only {nonZero} of {compared} pairs kern; the sample should exercise kerning.");
    }

    /// <summary>
    /// HarfBuzz as the oracle: with ligatures off, its advance for each glyph of a Latin string is the glyph's
    /// advance plus the pair kerning, which is what layout uses. Runs where hb-shape is installed (the Linux gate).
    /// </summary>
    [Fact]
    public void MatchesHarfBuzzAdvances()
    {
        string? fontPath = FontPath();
        string? hbShape = FindOnPath(OperatingSystem.IsWindows() ? "hb-shape.exe" : "hb-shape");
        if (fontPath is null || hbShape is null)
        {
            if (Environment.GetEnvironmentVariable("ZORG_REQUIRE_ORACLES") == "1")
            {
                Assert.Fail("hb-shape or the test font isn't available, and ZORG_REQUIRE_ORACLES=1.");
            }

            Assert.Skip("hb-shape isn't installed (it runs in the Linux gate).");
        }

        TrueTypeFont font = TrueTypeFont.Parse(File.ReadAllBytes(fontPath));
        var start = new ProcessStartInfo(hbShape) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in new[] { "--no-glyph-names", "--no-clusters", "--features=-liga,-clig,-dlig,-calt", fontPath, Sample })
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        // "[36+1366|57+1286|...]": glyph id + advance, with @x,y offsets when non-zero.
        var expected = new List<(int Glyph, int Advance)>();
        foreach (Match m in GlyphAdvance().Matches(output))
        {
            expected.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        }

        Assert.Equal(Sample.Length, expected.Count);
        for (int i = 0; i < Sample.Length; i++)
        {
            ushort glyph = font.GlyphFor(Sample[i]);
            int ours = font.AdvanceWidth(glyph) + (i + 1 < Sample.Length ? font.Kerning(glyph, font.GlyphFor(Sample[i + 1])) : 0);
            Assert.True(expected[i].Glyph == glyph, $"Glyph {i} ('{Sample[i]}'): HarfBuzz {expected[i].Glyph}, ours {glyph}.");
            Assert.True(expected[i].Advance == ours, $"Advance {i} ('{Sample[i]}'): HarfBuzz {expected[i].Advance}, ours {ours}.");
        }
    }

    private static int Kern(TrueTypeFont font, char left, char right) => font.Kerning(font.GlyphFor(left), font.GlyphFor(right));

    private static string? FontPath()
    {
        string? chosen = Environment.GetEnvironmentVariable("ZORG_PDF_TEST_FONT");
        return !string.IsNullOrEmpty(chosen) ? chosen : Array.Find(
            [@"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf"],
            File.Exists);
    }

    private static string? FindOnPath(string name)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(\d+)\+(-?\d+)")]
    private static partial Regex GlyphAdvance();
}
