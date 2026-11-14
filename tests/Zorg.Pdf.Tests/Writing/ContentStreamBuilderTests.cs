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
}
