using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Reading;
using Zorg.Pdf.Rendering;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf;

/// <summary>
/// Puts pages of existing PDFs together into a new file: merge whole files, take some pages out of one, split one
/// into several, or write a file again with new metadata. Pages are copied as they are (content, images, fonts, links
/// between copied pages, other annotations); what belonged to the old file as a whole (tags, bookmarks, form logic,
/// scripts) isn't kept, and each loss is noted in the result. Any PDF can be read; see <see cref="PdfFileInfo"/>.
/// The output is the same bytes for the same input.
/// </summary>
/// <example>
/// <code>
/// await using FileStream output = File.Create("pack.pdf");
/// PdfFileResult result = await PdfFiles.MergeAsync([new PdfPageSource(cover), new PdfPageSource(contract) { Pages = "2-" }], output,
///     new PdfFileOptions { Title = "Signing pack" }, cancellationToken);
/// </code>
/// </example>
public static class PdfFiles
{
    /// <summary>Writes the pages of several files, in order, as one.</summary>
    /// <param name="sources">The files, each with the pages to take.</param>
    /// <param name="output">Where the new file goes. It's written, flushed and left open.</param>
    /// <param name="options">Metadata for the new file, and read limits; null for the defaults (the first file's title, language and author).</param>
    /// <param name="cancellationToken">Stops writing.</param>
    /// <returns>The page count, size and notes on what wasn't kept.</returns>
    /// <exception cref="PdfReadException">A file can't be read, is encrypted, or exceeds a limit.</exception>
    /// <exception cref="ArgumentException">A page range is malformed or names a page the file doesn't have.</exception>
    public static async Task<PdfFileResult> MergeAsync(IEnumerable<PdfPageSource> sources, Stream output, PdfFileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(output);
        options ??= PdfFileOptions.Default;
        List<PdfPageSource> list = [.. sources];
        if (list.Count == 0)
        {
            throw new ArgumentException("There are no files to take pages from.", nameof(sources));
        }

        // Every file is read and every range checked before anything is written.
        var diagnostics = new List<PdfDiagnostic>();
        var opened = new List<(CosFile File, List<CosPage> Pages, int[] Selected)>();
        int total = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var read = new List<PdfDiagnostic>();
            var file = new CosFile(list[i].File, options.ReadLimits, read);
            if (file.IsEncrypted)
            {
                throw new PdfReadException($"File {i + 1} is encrypted, so its pages can't be copied.");
            }

            List<CosPage> pages = CosPages.Read(file, options.ReadLimits, read);
            int[] selected = PageRanges.Parse(list[i].Pages, pages.Count, $"file {i + 1}");
            total += selected.Length;
            if (total > options.ReadLimits.MaxPages)
            {
                throw new PdfReadException($"The new file would have more than {options.ReadLimits.MaxPages:N0} pages (PdfReadLimits.MaxPages).");
            }

            diagnostics.AddRange(read.Select(d => d with { Path = $"file {i + 1}" }));
            opened.Add((file, pages, selected));
        }

        if (total == 0)
        {
            throw new ArgumentException("No pages were selected.", nameof(sources));
        }

        using var writer = new PdfObjectWriter(output, PdfVersion.Pdf17, synchronous: false, objectStreams: true);
        PdfReference tree = writer.Allocate();
        var kids = new List<PdfReference>();
        var notes = new Notes(diagnostics);
        for (int i = 0; i < opened.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (CosFile file, List<CosPage> pages, int[] selected) = opened[i];
            var copier = new PageCopier(file, writer, options.ReadLimits, notes, $"file {i + 1}");
            kids.AddRange(copier.Copy(pages, selected, tree));
            if (file.Catalog["StructTreeRoot"] is not null and not CosNull)
            {
                notes.Add(DiagnosticCodes.FilesTagsDropped, $"File {i + 1} is tagged, but tags aren't kept when pages are copied: the new file isn't tagged, so it isn't accessible to screen readers as it was.", $"file {i + 1}");
            }

            if (file.Catalog["Outlines"] is not null and not CosNull)
            {
                notes.Add(DiagnosticCodes.FilesBookmarksDropped, $"File {i + 1}'s bookmarks aren't kept.", $"file {i + 1}");
            }

            await writer.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
        }

        writer.BeginObject(tree);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Pages"u8);
        writer.Name("Kids"u8);
        writer.BeginArray();
        foreach (PdfReference kid in kids)
        {
            writer.Reference(kid);
        }

        writer.EndArray();
        writer.Name("Count"u8);
        writer.Integer(kids.Count);
        writer.EndDictionary();
        writer.EndObject();

        // Metadata: what the options say, else the first file's.
        CosFile first = opened[0].File;
        CosDictionary? oldInfo = first.Resolve(first.Trailer["Info"]) as CosDictionary;
        string? Old(string key) => oldInfo is not null && first.Resolve(oldInfo[key]) is CosString s && CosPages.Text(s) is { Length: > 0 } t ? t : null;
        string? language = options.Language ?? (first.Resolve(first.Catalog["Lang"]) is CosString lang ? CosPages.Text(lang) : null);
        PdfReference info = writer.Allocate();
        writer.BeginObject(info);
        writer.BeginDictionary();
        foreach ((string key, string? value) in new[] { ("Title", options.Title ?? Old("Title")), ("Author", options.Author ?? Old("Author")), ("Subject", options.Subject ?? Old("Subject")), ("Keywords", options.Keywords ?? Old("Keywords")), ("Creator", Old("Creator")) })
        {
            if (value is not null)
            {
                writer.Name(System.Text.Encoding.ASCII.GetBytes(key));
                writer.TextString(value);
            }
        }

        writer.Name("Producer"u8);
        writer.TextString(DocumentWriter.Producer);
        if (options.ModificationDate is { } modified)
        {
            writer.Name("ModDate"u8);
            writer.TextString(DocumentWriter.PdfDate(modified));
        }

        writer.EndDictionary();
        writer.EndObject();

        PdfReference catalog = writer.Allocate();
        writer.BeginObject(catalog);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Catalog"u8);
        writer.Name("Pages"u8);
        writer.Reference(tree);
        if (language is { Length: > 0 })
        {
            writer.Name("Lang"u8);
            writer.TextString(language);
        }

        writer.EndDictionary();
        writer.EndObject();
        await writer.FinishAsync(catalog, info, cancellationToken).ConfigureAwait(false);
        return new PdfFileResult(kids.Count, writer.Position, [.. diagnostics]);
    }

    /// <summary>Writes some pages of one file as a new file.</summary>
    /// <param name="file">The file.</param>
    /// <param name="pages">The pages, such as "1-3,5" or "2-" (from 2 to the end); null for all.</param>
    /// <param name="output">Where the new file goes.</param>
    /// <param name="options">Metadata and limits; null for the file's own metadata.</param>
    /// <param name="cancellationToken">Stops writing.</param>
    /// <returns>The page count, size and notes on what wasn't kept.</returns>
    /// <exception cref="PdfReadException">The file can't be read, is encrypted, or exceeds a limit.</exception>
    /// <exception cref="ArgumentException">The page range is malformed or names a page the file doesn't have.</exception>
    public static Task<PdfFileResult> ExtractAsync(ReadOnlyMemory<byte> file, string? pages, Stream output, PdfFileOptions? options = null, CancellationToken cancellationToken = default) =>
        MergeAsync([new PdfPageSource(file) { Pages = pages }], output, options, cancellationToken);

    /// <summary>The note list, each code and message once.</summary>
    internal sealed class Notes(List<PdfDiagnostic> diagnostics)
    {
        public void Add(string code, string message, string path)
        {
            if (!diagnostics.Exists(d => d.Code == code && d.Path == path))
            {
                diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Info, message, path));
            }
        }
    }
}

/// <summary>A file to take pages from, for <see cref="PdfFiles.MergeAsync"/>.</summary>
/// <param name="File">The whole file.</param>
public sealed record PdfPageSource(ReadOnlyMemory<byte> File)
{
    /// <summary>The pages to take, in the order given: "1-3,5", "2-" (2 to the end), "-3" (1 to 3) or "4,1"; null for all.</summary>
    public string? Pages { get; init; }
}

/// <summary>Metadata for a file <see cref="PdfFiles"/> writes, and limits for reading its sources.</summary>
public sealed record PdfFileOptions
{
    /// <summary>No changes: the first file's metadata, the default limits.</summary>
    public static PdfFileOptions Default { get; } = new();

    /// <summary>The title; null keeps the first file's.</summary>
    public string? Title { get; init; }

    /// <summary>The author; null keeps the first file's.</summary>
    public string? Author { get; init; }

    /// <summary>The subject; null keeps the first file's.</summary>
    public string? Subject { get; init; }

    /// <summary>The keywords; null keeps the first file's.</summary>
    public string? Keywords { get; init; }

    /// <summary>The language (BCP 47); null keeps the first file's.</summary>
    public string? Language { get; init; }

    /// <summary>The modification date to write. Null writes none, so the same input gives the same bytes.</summary>
    public DateTimeOffset? ModificationDate { get; init; }

    /// <summary>Limits for reading the source files.</summary>
    public PdfReadLimits ReadLimits { get; init; } = PdfReadLimits.Default;
}

/// <summary>What <see cref="PdfFiles"/> wrote.</summary>
public sealed class PdfFileResult
{
    internal PdfFileResult(int pageCount, long bytesWritten, ImmutableArray<PdfDiagnostic> diagnostics) =>
        (PageCount, BytesWritten, Diagnostics) = (pageCount, bytesWritten, diagnostics);

    /// <summary>The number of pages written.</summary>
    public int PageCount { get; }

    /// <summary>The size of the file written.</summary>
    public long BytesWritten { get; }

    /// <summary>What wasn't kept (tags, bookmarks, form logic, links to pages left out) and how the sources were read (ZPDF-E and P codes).</summary>
    public ImmutableArray<PdfDiagnostic> Diagnostics { get; }
}

/// <summary>Page ranges such as "1-3,5,8-".</summary>
internal static class PageRanges
{
    /// <summary>The pages, from 0, in the order given; all when the text is null or empty.</summary>
    public static int[] Parse(string? text, int count, string where)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [.. Enumerable.Range(0, count)];
        }

        var pages = new List<int>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-', StringComparison.Ordinal);
            (string from, string to) = dash < 0 ? (part, part) : (part[..dash].Trim(), part[(dash + 1)..].Trim());
            int first = from.Length == 0 ? 1 : Number(from), last = to.Length == 0 ? count : Number(to);
            if (first < 1 || last > count || first > last)
            {
                throw new ArgumentException($"The pages \"{part}\" aren't in {where}, which has {count} page{(count == 1 ? "" : "s")}.");
            }

            for (int p = first; p <= last; p++)
            {
                pages.Add(p - 1);
            }
        }

        return [.. pages];

        int Number(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new ArgumentException($"\"{text}\" isn't a page range: use numbers like \"1-3,5\" or \"2-\".");
    }
}
