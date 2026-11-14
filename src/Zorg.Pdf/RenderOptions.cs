using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Json;

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
public sealed class RenderOptions
{
    /// <summary>No fonts, no conformance claim, default limits. Rendering with it fails until fonts are added.</summary>
    public static RenderOptions Default { get; } = new();

    /// <summary>The fonts text may use. Required: there are no system fonts by default.</summary>
    public FontCollection Fonts { get; init; } = FontCollection.Empty;

    /// <summary>
    /// A standard to claim and check. Output is always tagged; <see cref="PdfConformance.PdfUA1"/> also writes the
    /// PDF/UA identifier and turns accessibility warnings into errors.
    /// </summary>
    public PdfConformance Conformance { get; init; }

    /// <summary>Limits that stop a runaway render.</summary>
    public PdfLimits Limits { get; init; } = PdfLimits.Default;

    /// <summary>The creation date to write. Null (the default) writes none, so the same input gives the same bytes.</summary>
    public DateTimeOffset? CreationDate { get; init; }

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

/// <summary>Limits that stop a render before it uses unbounded time or memory. Exceeding one throws <see cref="PdfLimitException"/>.</summary>
public sealed record PdfLimits
{
    /// <summary>10,000 pages.</summary>
    public static PdfLimits Default { get; } = new();

    /// <summary>The most pages a render may produce.</summary>
    public int MaxPages { get; init; } = 10_000;
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
