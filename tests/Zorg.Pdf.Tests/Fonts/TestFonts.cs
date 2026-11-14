using System;
using System.IO;
using Xunit;

namespace Zorg.Pdf.Tests.Fonts;

/// <summary>
/// Finds a TrueType font installed on the machine, so no font file is committed or downloaded: Arial on Windows and
/// macOS, DejaVu Sans on Linux (Alpine's font-dejavu, Debian and Ubuntu's fonts-dejavu-core). ZORG_PDF_TEST_FONT
/// overrides the search. Tests skip when none is found, unless ZORG_REQUIRE_FONTS=1, which the Linux gate sets.
/// </summary>
internal static class TestFonts
{
    private static readonly string[] Candidates =
    [
        @"C:\Windows\Fonts\arial.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/Library/Fonts/Arial.ttf",
    ];

    private static readonly Lazy<byte[]?> Cached = new(Load);

    /// <summary>The font's bytes, or a skip (a failure when fonts are required).</summary>
    public static byte[] Require()
    {
        if (Cached.Value is { } font)
        {
            return font;
        }

        const string Message = "No TrueType test font found. Install DejaVu Sans or Arial, or set ZORG_PDF_TEST_FONT.";
        if (Environment.GetEnvironmentVariable("ZORG_REQUIRE_FONTS") == "1")
        {
            Assert.Fail(Message);
        }

        Assert.Skip(Message);
        return [];
    }

    /// <summary>Another face, or a fallback font, by kind; a skip (a failure when fonts are required) if missing.</summary>
    public static byte[] Require(string kind)
    {
        string[] candidates = kind switch
        {
            "bold" => [@"C:\Windows\Fonts\arialbd.ttf", "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"],
            "italic" => [@"C:\Windows\Fonts\ariali.ttf", "/usr/share/fonts/dejavu/DejaVuSans-Oblique.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Oblique.ttf"],
            "devanagari" => [@"C:\Windows\Fonts\Nirmala.ttf", "/usr/share/fonts/noto/NotoSansDevanagari-Regular.ttf", "/usr/share/fonts/truetype/noto/NotoSansDevanagari-Regular.ttf"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown font kind."),
        };
        if (Array.Find(candidates, File.Exists) is { } path)
        {
            return File.ReadAllBytes(path);
        }

        string message = $"No {kind} test font found.";
        if (Environment.GetEnvironmentVariable("ZORG_REQUIRE_FONTS") == "1")
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
        return [];
    }

    private static byte[]? Load()
    {
        string? chosen = Environment.GetEnvironmentVariable("ZORG_PDF_TEST_FONT");
        if (string.IsNullOrEmpty(chosen))
        {
            chosen = Array.Find(Candidates, File.Exists);
        }

        return chosen is null ? null : File.ReadAllBytes(chosen);
    }
}
