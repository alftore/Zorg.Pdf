using System.Text.Json.Serialization;

namespace Zorg.Pdf;

/// <summary>
/// A page of an existing PDF drawn behind the document's pages: letterhead, stationery or a form to fill in. The
/// first page can have one page of it (a full letterhead) and the pages after another (a plain continuation sheet),
/// or none. It's drawn as it is, vector and sharp, as an artifact that screen readers skip; a page of another size
/// is scaled to fit and centred. The file comes from a data slot or bytes, like an image.
/// In JSON <c>"background": { "source": { "$data": "letterhead" }, "otherPages": 2 }</c>.
/// </summary>
/// <example>
/// <code>
/// var letter = document with { Background = new PageBackground(ImageSource.FromSlot("letterhead")) { OtherPages = 2 } };
/// var options = new RenderOptions { Fonts = fonts, Data = PdfData.Empty.WithPdf("letterhead", await File.ReadAllBytesAsync("letterhead.pdf", cancellationToken)) };
/// </code>
/// </example>
public sealed record PageBackground
{
    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    internal PageBackground(ImageSource source, int firstPage = 1, int otherPages = 1) =>
        (Source, FirstPage, OtherPages) = (source, firstPage, otherPages);

    /// <summary>A background from a PDF file.</summary>
    /// <param name="source">The PDF's bytes, or a data slot that holds them.</param>
    public PageBackground(ImageSource source) => Source = source;

    /// <summary>The PDF file: its bytes, or a data slot (<see cref="PdfData.WithPdf(string, System.ReadOnlyMemory{byte})"/>).</summary>
    public ImageSource Source { get; init; }

    /// <summary>The page of the file, from 1, behind the document's first page; 0 for none. Default 1.</summary>
    public int FirstPage { get; init; } = 1;

    /// <summary>The page of the file, from 1, behind every page after the first; 0 for none. Default 1, the same as the first.</summary>
    public int OtherPages { get; init; } = 1;
}
