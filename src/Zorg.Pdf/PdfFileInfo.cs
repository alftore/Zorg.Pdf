using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Reading;

namespace Zorg.Pdf;

/// <summary>
/// What an existing PDF file holds: its pages and their sizes, its metadata, and whether it's encrypted or tagged.
/// Any PDF can be read, not only Zorg.Pdf's: incremental updates, object streams and the common filters are
/// followed, and a damaged file is read the way viewers read it, with a diagnostic. Reading is bounded by
/// <see cref="PdfReadLimits"/>, so a hostile file fails with a <see cref="PdfReadException"/> instead of using unbounded
/// time or memory.
/// </summary>
/// <example>
/// <code>
/// PdfFileInfo info = await PdfFileInfo.ReadAsync(File.OpenRead("contract.pdf"), cancellationToken: cancellationToken);
/// Console.WriteLine($"{info.Title}: {info.PageCount} pages, the first {info.Pages[0].Width:0} × {info.Pages[0].Height:0} pt");
/// </code>
/// </example>
public sealed class PdfFileInfo
{
    private PdfFileInfo()
    {
    }

    /// <summary>The PDF version, such as "1.7": the header's, or the catalog's when that is later.</summary>
    public required string Version { get; init; }

    /// <summary>The pages in order.</summary>
    public required ImmutableArray<PdfPageInfo> Pages { get; init; }

    /// <summary>The number of pages.</summary>
    public int PageCount => Pages.Length;

    /// <summary>
    /// True when the file is encrypted. Its page sizes are still read when they can be, but not its metadata. A file
    /// encrypted to stop copying or printing, with no password to open it, is still encrypted.
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>True when the file is tagged (it has a structure tree and says it's marked), as accessible PDFs are.</summary>
    public bool IsTagged { get; init; }

    /// <summary>The document's title, or null.</summary>
    public string? Title { get; init; }

    /// <summary>The author, or null.</summary>
    public string? Author { get; init; }

    /// <summary>The subject, or null.</summary>
    public string? Subject { get; init; }

    /// <summary>The keywords, or null.</summary>
    public string? Keywords { get; init; }

    /// <summary>The application that made the original document, or null.</summary>
    public string? Creator { get; init; }

    /// <summary>The application that wrote the PDF, or null.</summary>
    public string? Producer { get; init; }

    /// <summary>The document's language (the catalog's <c>/Lang</c>), or null.</summary>
    public string? Language { get; init; }

    /// <summary>What was wrong with the file and how it was read anyway: ZPDF-P001 to P004.</summary>
    public ImmutableArray<PdfDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Reads a file in memory.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="limits">Limits on size and work; null for <see cref="PdfReadLimits.Default"/>.</param>
    /// <returns>What the file holds.</returns>
    /// <exception cref="PdfReadException">The file isn't a PDF, is too damaged to read, or exceeds a limit.</exception>
    public static PdfFileInfo Read(ReadOnlyMemory<byte> file, PdfReadLimits? limits = null)
    {
        limits ??= PdfReadLimits.Default;
        var diagnostics = new List<PdfDiagnostic>();
        var cos = new CosFile(file, limits, diagnostics);
        CosDictionary catalog = cos.Catalog;
        List<CosPage> pages = CosPages.Read(cos, limits, diagnostics);
        CosDictionary? info = cos.IsEncrypted ? null : cos.Resolve(cos.Trailer["Info"]) as CosDictionary;
        string? Get(CosDictionary? from, string key) =>
            from is not null && cos.Resolve(from[key]) is CosString s && CosPages.Text(s) is { Length: > 0 } text ? text.TrimEnd('\0') : null;
        string version = cos.Version;
        if (cos.Resolve(catalog["Version"]) is CosName { Value: { } later } && string.CompareOrdinal(later, version) > 0 && later.Length <= 4)
        {
            version = later;
        }

        bool marked = cos.Resolve(catalog["MarkInfo"]) is CosDictionary mark && cos.Resolve(mark["Marked"]) is CosBoolean { Value: true };
        return new PdfFileInfo
        {
            Version = version,
            Pages = [.. pages.Select((p, i) => PdfPageInfo.From(i + 1, p))],
            IsEncrypted = cos.IsEncrypted,
            IsTagged = marked && catalog["StructTreeRoot"] is not null and not CosNull,
            Title = Get(info, "Title"),
            Author = Get(info, "Author"),
            Subject = Get(info, "Subject"),
            Keywords = Get(info, "Keywords"),
            Creator = Get(info, "Creator"),
            Producer = Get(info, "Producer"),
            Language = cos.IsEncrypted ? null : Get(catalog, "Lang"),
            Diagnostics = [.. diagnostics],
        };
    }

    /// <summary>Reads a file from a stream, which is read to its end (up to <see cref="PdfReadLimits.MaxFileBytes"/>).</summary>
    /// <param name="stream">The file.</param>
    /// <param name="limits">Limits on size and work; null for <see cref="PdfReadLimits.Default"/>.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>What the file holds.</returns>
    /// <exception cref="PdfReadException">The file isn't a PDF, is too damaged to read, or exceeds a limit.</exception>
    public static async Task<PdfFileInfo> ReadAsync(Stream stream, PdfReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= PdfReadLimits.Default;
        byte[] file = await ReadAllAsync(stream, limits.MaxFileBytes, cancellationToken).ConfigureAwait(false);
        return Read(file, limits);
    }

    /// <summary>Reads a stream to its end, failing once it passes a size.</summary>
    internal static async Task<byte[]> ReadAllAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (buffer.Length > maxBytes)
            {
                throw new PdfReadException($"The file is over the limit of {maxBytes:N0} bytes (PdfReadLimits.MaxFileBytes).");
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>A page of an existing PDF.</summary>
/// <param name="Number">The page's number, from 1.</param>
/// <param name="Width">The visible width in points as the page is shown, after its rotation (the crop box, else the media box).</param>
/// <param name="Height">The visible height in points as the page is shown, after its rotation.</param>
/// <param name="Rotation">The rotation viewers apply: 0, 90, 180 or 270 degrees clockwise.</param>
public sealed record PdfPageInfo(int Number, double Width, double Height, int Rotation)
{
    internal static PdfPageInfo From(int number, CosPage page)
    {
        double width = page.CropBox[2] - page.CropBox[0], height = page.CropBox[3] - page.CropBox[1];
        return page.Rotate is 90 or 270 ? new(number, height, width, page.Rotate) : new(number, width, height, page.Rotate);
    }
}

/// <summary>Limits that stop reading an existing PDF before it uses unbounded time or memory. Exceeding one throws <see cref="PdfReadException"/>.</summary>
public sealed record PdfReadLimits
{
    /// <summary>The defaults: 500 MB files, a million objects, 100,000 pages.</summary>
    public static PdfReadLimits Default { get; } = new();

    /// <summary>The largest file: 500 MB. At most about 2 GB, the most a single buffer holds.</summary>
    public long MaxFileBytes { get; init; } = 500_000_000;

    /// <summary>The most objects read or listed: one million.</summary>
    public int MaxObjects { get; init; } = 1_000_000;

    /// <summary>The most one stream may decode to: 100 MB.</summary>
    public long MaxStreamBytes { get; init; } = 100_000_000;

    /// <summary>The most all streams together may decode to: 1 GB. Stops many small decompression bombs.</summary>
    public long MaxDecodedBytes { get; init; } = 1_000_000_000;

    /// <summary>The most pages: 100,000.</summary>
    public int MaxPages { get; init; } = 100_000;
}

/// <summary>An existing PDF couldn't be read: it isn't a PDF, is too damaged, uses something not supported, or exceeds a <see cref="PdfReadLimits"/> limit.</summary>
public class PdfReadException : PdfException
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfReadException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public PdfReadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public PdfReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static PdfReadException Malformed(string detail) => new($"The PDF file is damaged: {detail}");
}
