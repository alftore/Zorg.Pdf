using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xunit;

namespace Zorg.Pdf.Tests.Writing;

/// <summary>
/// Just enough reading to check what the writer produced: the trailer, and that every cross-reference entry points at
/// the object it names. Not a parser; the oracle tests (qpdf, veraPDF) do the real checking.
/// </summary>
internal static class PdfFile
{
    public static string Text(byte[] file) => Encoding.Latin1.GetString(file);

    /// <summary>Checks the header, the xref table and every offset in it. Returns the object offsets by number.</summary>
    public static IReadOnlyList<long> CheckStructure(byte[] file)
    {
        string text = Text(file);
        Assert.StartsWith("%PDF-1.", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        int startxref = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        Assert.True(startxref > 0, "No startxref.");
        int numberStart = startxref + "startxref\n".Length;
        long xref = long.Parse(text.AsSpan(numberStart, text.IndexOf('\n', numberStart) - numberStart), CultureInfo.InvariantCulture);
        Assert.Equal("xref\n0 ", text.Substring((int)xref, 7));

        int lineEnd = text.IndexOf('\n', (int)xref + 5);
        int count = int.Parse(text.AsSpan((int)xref + 7, lineEnd - ((int)xref + 7)), CultureInfo.InvariantCulture);
        int entries = lineEnd + 1;
        Assert.Equal("0000000000 65535 f\r\n", text.Substring(entries, 20));

        var offsets = new List<long> { -1 };
        for (int i = 1; i < count; i++)
        {
            string entry = text.Substring(entries + (i * 20), 20);
            Assert.EndsWith(" 00000 n\r\n", entry, StringComparison.Ordinal);
            long offset = long.Parse(entry.AsSpan(0, 10), CultureInfo.InvariantCulture);
            Assert.Equal($"{i} 0 obj\n", text.Substring((int)offset, $"{i} 0 obj\n".Length));
            offsets.Add(offset);
        }

        Assert.StartsWith("trailer\n<</Size " + count.ToString(CultureInfo.InvariantCulture), text[(entries + (count * 20))..], StringComparison.Ordinal);
        return offsets;
    }
}
