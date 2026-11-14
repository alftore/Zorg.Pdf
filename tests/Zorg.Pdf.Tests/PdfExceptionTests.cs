using System;
using Xunit;

namespace Zorg.Pdf.Tests;

public sealed class PdfExceptionTests
{
    [Fact]
    public void KeepsMessageAndCause()
    {
        var cause = new InvalidOperationException("cause");
        var exception = new PdfException("Font 'Inter' has no glyph for U+0644.", cause);

        Assert.Equal("Font 'Inter' has no glyph for U+0644.", exception.Message);
        Assert.Same(cause, exception.InnerException);
    }
}
