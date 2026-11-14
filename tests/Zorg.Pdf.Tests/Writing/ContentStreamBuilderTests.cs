using System;
using System.Text;
using Xunit;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Tests.Writing;

public sealed class ContentStreamBuilderTests
{
    [Fact]
    public void AdjustmentsSplitTheStringAndAreNegatedForTj()
    {
        var builder = new ContentStreamBuilder().ShowGlyphs([0x24, 0x39, 0x03, 0x37], [-74.2187, 0, 0, 25]);

        // The last adjustment is ignored: nothing follows the last glyph.
        Assert.Equal("[<0024>74.2187<003900030037>] TJ\n", Encoding.ASCII.GetString(builder.WrittenSpan));
    }

    [Fact]
    public void NoAdjustmentsGiveAPlainTj()
    {
        var builder = new ContentStreamBuilder().ShowGlyphs([1, 2], [0.00001, 0]);
        Assert.Equal("<00010002> Tj\n", Encoding.ASCII.GetString(builder.WrittenSpan));
    }

    [Fact]
    public void PathsAreWrittenOneOperatorPerLine()
    {
        var builder = new ContentStreamBuilder()
            .SetLineCap(1).SetLineJoin(2).SetDash([3, 1.5], 0.5)
            .MoveTo(10, 20).LineTo(30.25, 20).CurveTo(40, 20, 50, 30, 50, 40).ClosePath().Stroke()
            .Rectangle(0, 0, 5, 5).Clip(evenOdd: true).EndPath()
            .Rectangle(1, 1, 2, 2).FillAndStroke().Rectangle(1, 1, 2, 2).Fill(evenOdd: true)
            .SetDash([]);

        Assert.Equal(
            "1 J\n2 j\n[3 1.5] 0.5 d\n10 20 m\n30.25 20 l\n40 20 50 30 50 40 c\nh\nS\n0 0 5 5 re\nW*\nn\n1 1 2 2 re\nB\n1 1 2 2 re\nf*\n[] 0 d\n",
            Encoding.ASCII.GetString(builder.WrittenSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentStreamBuilder().SetLineCap(3));
    }
}
