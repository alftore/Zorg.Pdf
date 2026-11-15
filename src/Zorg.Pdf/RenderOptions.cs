using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Json;
using Zorg.Pdf.Signing;

namespace Zorg.Pdf;

/// <summary>What a render needs besides the document: fonts, conformance, limits, and an optional creation date.</summary>
/// <example>
/// <code>
/// var options = new RenderOptions
/// {
///     Fonts = FontCollection.Empty.Add(new FontFamily("Inter") { Regular = FontSource.FromFile("Inter-Regular.ttf") }),
///     Conformance = PdfConformance.PdfUA1,
/// };
/// RenderResult result = await document.RenderAsync(response.Body, options, cancellationToken);
/// </code>
/// </example>
public sealed record RenderOptions
{
    /// <summary>No fonts, no conformance claim, default limits. Rendering with it fails until fonts are added.</summary>
    public static RenderOptions Default { get; } = new();

    /// <summary>The fonts text may use. There are no system fonts by default; see <see cref="FontResolver"/>.</summary>
    public FontCollection Fonts { get; init; } = FontCollection.Empty;

    /// <summary>
    /// Finds families the <see cref="Fonts"/> collection doesn't have, such as <see cref="FontResolvers.System"/>.
    /// Null (the default): only the collection is used, so output doesn't depend on the machine.
    /// </summary>
    public IFontResolver? FontResolver { get; init; }

    /// <summary>
    /// A standard to claim and check. Output is always tagged; <see cref="PdfConformance.PdfUA1"/> also writes the
    /// PDF/UA identifier and turns accessibility warnings into errors.
    /// </summary>
    public PdfConformance Conformance { get; init; }

    /// <summary>
    /// A PDF/A level to claim, for archiving: the file then carries an sRGB output intent and the PDF/A identifier,
    /// and what PDF/A forbids (CMYK images without a CMYK output intent, characters no font has) stops the render.
    /// Independent of <see cref="Conformance"/>: a file can be PDF/A-2a and PDF/UA-1 at once.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1, Archive = PdfArchive.PdfA2a };
    /// </code>
    /// </example>
    public PdfArchive Archive { get; init; }

    /// <summary>Rows for tables bound to data slots, read as the tables are laid out.</summary>
    public PdfData Data { get; init; } = PdfData.Empty;

    /// <summary>Limits that stop a runaway render.</summary>
    public PdfLimits Limits { get; init; } = PdfLimits.Default;

    /// <summary>
    /// Prepares images before they're embedded: other formats, orientation, colour, shrinking. Null: JPEG and PNG
    /// are read as they are. <c>Zorg.Pdf.Imaging</c> has one built on Zorg.Imaging.
    /// </summary>
    public IImageProcessor? ImageProcessor { get; init; }

    /// <summary>
    /// Hyphenates words in text whose style sets <see cref="Hyphens.Auto"/>, such as <c>PatternHyphenator.Default</c>
    /// from <c>Zorg.Pdf.Hyphenation</c>. Null: only soft hyphens break words.
    /// </summary>
    public IHyphenator? Hyphenator { get; init; }

    /// <summary>The creation date to write. Null (the default) writes none, so the same input gives the same bytes.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>Extra XMP metadata, such as Factur-X's <c>fx:</c> properties. Described in the PDF/A extension schema under PDF/A.</summary>
    public ImmutableArray<XmpSchema> MetadataSchemas { get; init; } = [];

    /// <summary>
    /// Signs the document as it's written: a PAdES B-B signature in an invisible field. Null (the default): unsigned.
    /// The key is only ever given here, in code; a JSON document can't carry one.
    /// </summary>
    public PdfSignatureOptions? Signature { get; init; }

    /// <summary>Told after each page reaches the output.</summary>
    public IProgress<RenderProgress>? Progress { get; init; }
}

/// <summary>A standard a document claims to meet.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PdfConformance>))]
public enum PdfConformance
{
    /// <summary>No claim. The output is still tagged.</summary>
    None,

    /// <summary>PDF/UA-1 (ISO 14289-1), universal accessibility. Checked by veraPDF in the project's tests.</summary>
    PdfUA1,
}

/// <summary>A PDF/A level (ISO 19005-2 or -3) a document claims, for long-term archiving. PDF/A-3 is PDF/A-2 with attachments.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PdfArchive>))]
public enum PdfArchive
{
    /// <summary>No claim.</summary>
    None,

    /// <summary>PDF/A-2b: looks the same in years to come (fonts embedded, colour defined, no external content).</summary>
    PdfA2b,

    /// <summary>PDF/A-2u: as 2b, and every character maps to Unicode, so text can be searched and copied.</summary>
    PdfA2u,

    /// <summary>
    /// PDF/A-2a: as 2u, and tagged with a logical structure, as Zorg.Pdf's output always is. Accessibility problems
    /// (images without alt text, skipped heading levels) stop the render, as under PDF/UA.
    /// </summary>
    PdfA2a,

    /// <summary>PDF/A-3b: as PDF/A-2b, and files may be attached (<see cref="PdfDocument.Attachments"/>), such as an e-invoice's XML.</summary>
    PdfA3b,

    /// <summary>PDF/A-3u: as PDF/A-2u, with attachments.</summary>
    PdfA3u,

    /// <summary>PDF/A-3a: as PDF/A-2a, with attachments.</summary>
    PdfA3a,
}

/// <summary>What a <see cref="PdfArchive"/> level means.</summary>
internal static class PdfArchiveLevels
{
    /// <summary>2 or 3; 0 for none.</summary>
    public static int Part(this PdfArchive archive) => archive switch
    {
        PdfArchive.None => 0,
        PdfArchive.PdfA3a or PdfArchive.PdfA3u or PdfArchive.PdfA3b => 3,
        _ => 2,
    };

    /// <summary>A, U or B.</summary>
    public static string Level(this PdfArchive archive) => archive switch
    {
        PdfArchive.PdfA2a or PdfArchive.PdfA3a => "A",
        PdfArchive.PdfA2u or PdfArchive.PdfA3u => "U",
        _ => "B",
    };
}

/// <summary>Limits that stop a render before it uses unbounded time or memory. Exceeding one throws <see cref="PdfLimitException"/>.</summary>
public sealed record PdfLimits
{
    /// <summary>10,000 pages.</summary>
    public static PdfLimits Default { get; } = new();

    /// <summary>The most pages a render may produce.</summary>
    public int MaxPages { get; init; } = 10_000;

    /// <summary>The most body rows one table may have.</summary>
    public int MaxTableRows { get; init; } = 1_000_000;

    /// <summary>The largest image file, in bytes: 50 MB.</summary>
    public long MaxImageBytes { get; init; } = 50_000_000;

    /// <summary>
    /// The most pixels one image may have: 50 million. A PNG with transparency is decoded, at one byte per channel
    /// per pixel, so this bounds the memory a render needs for it.
    /// </summary>
    public long MaxImagePixels { get; init; } = 50_000_000;

    /// <summary>The largest attachment, in bytes: 50 MB.</summary>
    public long MaxAttachmentBytes { get; init; } = 50_000_000;
}

/// <summary>Progress of a render: pages and bytes written so far.</summary>
/// <param name="PagesWritten">Pages written to the output.</param>
/// <param name="BytesWritten">Bytes passed to the output stream so far, rounded down to the last flush.</param>
public readonly record struct RenderProgress(int PagesWritten, long BytesWritten);

/// <summary>What a render produced. Errors stop a render, so its diagnostics are warnings and information only.</summary>
public sealed class RenderResult
{
    internal RenderResult(int pageCount, long bytesWritten, ImmutableArray<PdfDiagnostic> diagnostics) =>
        (PageCount, BytesWritten, Diagnostics) = (pageCount, bytesWritten, diagnostics);

    /// <summary>The number of pages.</summary>
    public int PageCount { get; }

    /// <summary>The size of the file written.</summary>
    public long BytesWritten { get; }

    /// <summary>Warnings and information, such as words broken because they were wider than the page.</summary>
    public ImmutableArray<PdfDiagnostic> Diagnostics { get; }

    /// <summary>True when there's at least one warning.</summary>
    public bool HasWarnings => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>A <see cref="PdfLimits"/> limit was exceeded. The output stream has the bytes written before it.</summary>
public class PdfLimitException : PdfException
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfLimitException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PdfLimitException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PdfLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
