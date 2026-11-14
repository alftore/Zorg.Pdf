using System;
using Zorg.Imaging;

namespace Zorg.Pdf.Imaging;

/// <summary>How a page preview is drawn.</summary>
/// <example>
/// <code>
/// byte[] png = await PdfPreview.RenderPagePngAsync(pdf, page: 0, new PreviewOptions { Dpi = 150 });
/// </code>
/// </example>
public sealed record PreviewOptions
{
    /// <summary>Defaults: 96 dpi on white.</summary>
    public static PreviewOptions Default { get; } = new();

    /// <summary>Pixels per inch: 96 by default (an A4 page is 794 × 1123 pixels), from 18 to 600.</summary>
    public double Dpi { get; init; } = 96;

    /// <summary>The paper colour behind the page: white by default.</summary>
    public Rgba32 Background { get; init; } = new(255, 255, 255);

    /// <summary>Limits on what a file may make the preview read and draw.</summary>
    public PdfPreviewLimits Limits { get; init; } = PdfPreviewLimits.Default;
}

/// <summary>Limits that keep a preview of a hostile or damaged file from using unbounded time or memory.</summary>
public sealed record PdfPreviewLimits
{
    /// <summary>The defaults: generous for any document Zorg.Pdf writes.</summary>
    public static PdfPreviewLimits Default { get; } = new();

    /// <summary>The largest file: 256 MB.</summary>
    public long MaxFileBytes { get; init; } = 256L << 20;

    /// <summary>The most objects read from the file: 1,000,000.</summary>
    public int MaxObjects { get; init; } = 1_000_000;

    /// <summary>The most bytes one stream may inflate to: 256 MB.</summary>
    public long MaxStreamBytes { get; init; } = 256L << 20;

    /// <summary>The most content stream operators on one page: 2,000,000.</summary>
    public int MaxOperators { get; init; } = 2_000_000;

    /// <summary>The most pixels of one embedded image: 50 million.</summary>
    public long MaxImagePixels { get; init; } = 50_000_000;
}

/// <summary>
/// A preview couldn't be drawn: the file isn't one Zorg.Pdf wrote (previews read only Zorg.Pdf's own output), it's
/// damaged, or it's over a <see cref="PdfPreviewLimits"/> limit. The message says which.
/// </summary>
public class PdfPreviewException : PdfException
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfPreviewException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PdfPreviewException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PdfPreviewException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static PdfPreviewException Malformed(string detail) => new($"The PDF can't be read: {detail}");
}
