using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using System.Text;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>Page geometry in points, after orientation.</summary>
internal readonly record struct PageGeometry(double Width, double Height, double Left, double Right, double Top, double Bottom)
{
    public double ContentWidth => Right - Left;

    public double ContentHeight => Top - Bottom;

    public static PageGeometry From(PageSettings page)
    {
        bool landscape = page.Orientation == PageOrientation.Landscape;
        double width = landscape ? page.Size.Height.Points : page.Size.Width.Points;
        double height = landscape ? page.Size.Width.Points : page.Size.Height.Points;
        Thickness m = page.Margin;
        return new PageGeometry(width, height, m.Left.Points, width - m.Right.Points, height - m.Top.Points, m.Bottom.Points);
    }
}

/// <summary>
/// The document-level objects: each page's content stream and page object as it finishes, then the page tree, the
/// Info dictionary and XMP metadata (with the same title, producer and dates), and the catalog.
/// </summary>
internal sealed class DocumentWriter
{
    /// <summary>"Zorg.Pdf 0.1.0": the same for every file a build writes, so output stays deterministic.</summary>
    internal static readonly string Producer = "Zorg.Pdf " + (typeof(DocumentWriter).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0");

    private readonly PdfObjectWriter _writer;
    private readonly PageGeometry _geometry;
    private readonly List<PdfReference> _pages = [];

    public DocumentWriter(PdfObjectWriter writer, PageGeometry geometry)
    {
        _writer = writer;
        _geometry = geometry;
        Catalog = writer.Allocate();
        PageTree = writer.Allocate();
    }

    public PdfReference Catalog { get; }

    public PdfReference PageTree { get; }

    public IReadOnlyList<PdfReference> Pages => _pages;

    /// <summary>Reserves the next page's object.</summary>
    public PdfReference AddPage()
    {
        PdfReference page = _writer.Allocate();
        _pages.Add(page);
        return page;
    }

    /// <summary>
    /// Writes a finished page: its content stream, then the page object with its font resources. A second content
    /// stream, <paramref name="overlay"/>, is drawn after the first and may be written later (the header and footer,
    /// which need the page count).
    /// </summary>
    public void WritePage(int index, ReadOnlySpan<byte> content, IEnumerable<(byte[] Name, PdfReference Font)> fonts, IReadOnlyCollection<(byte[] Name, PdfReference Image)> images, PdfReference? overlay = null)
    {
        PdfReference stream = _writer.Allocate();
        _writer.BeginStreamObject(stream);
        _writer.EndStreamObject(content, compress: true);

        _writer.BeginObject(_pages[index]);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Page"u8);
        _writer.Name("Parent"u8);
        _writer.Reference(PageTree);
        _writer.Name("MediaBox"u8);
        _writer.BeginArray();
        _writer.Integer(0);
        _writer.Integer(0);
        _writer.Real(_geometry.Width);
        _writer.Real(_geometry.Height);
        _writer.EndArray();
        _writer.Name("Resources"u8);
        _writer.BeginDictionary();
        _writer.Name("Font"u8);
        _writer.BeginDictionary();
        foreach ((byte[] name, PdfReference font) in fonts)
        {
            _writer.Name(name);
            _writer.Reference(font);
        }

        _writer.EndDictionary();
        if (images.Count > 0)
        {
            _writer.Name("XObject"u8);
            _writer.BeginDictionary();
            foreach ((byte[] name, PdfReference image) in images)
            {
                _writer.Name(name);
                _writer.Reference(image);
            }

            _writer.EndDictionary();
        }

        _writer.EndDictionary();
        _writer.Name("Contents"u8);
        if (overlay is { } second)
        {
            _writer.BeginArray();
            _writer.Reference(stream);
            _writer.Reference(second);
            _writer.EndArray();
        }
        else
        {
            _writer.Reference(stream);
        }

        _writer.Name("StructParents"u8);
        _writer.Integer(index);
        _writer.Name("Tabs"u8);
        _writer.Name("S"u8);
        _writer.EndDictionary();
        _writer.EndObject();
    }

    /// <summary>Writes the page tree, Info, XMP and catalog. Returns the Info dictionary for the trailer.</summary>
    public PdfReference Finish(PdfDocument document, RenderOptions options, PdfReference structTreeRoot)
    {
        _writer.BeginObject(PageTree);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Pages"u8);
        _writer.Name("Kids"u8);
        _writer.BeginArray();
        foreach (PdfReference page in _pages)
        {
            _writer.Reference(page);
        }

        _writer.EndArray();
        _writer.Name("Count"u8);
        _writer.Integer(_pages.Count);
        _writer.EndDictionary();
        _writer.EndObject();

        PdfReference info = _writer.Allocate();
        _writer.BeginObject(info);
        _writer.BeginDictionary();
        _writer.Name("Title"u8);
        _writer.TextString(document.Title);
        _writer.Name("Producer"u8);
        _writer.TextString(Producer);
        if (options.CreationDate is { } created)
        {
            _writer.Name("CreationDate"u8);
            _writer.TextString(PdfDate(created));
            _writer.Name("ModDate"u8);
            _writer.TextString(PdfDate(created));
        }

        _writer.EndDictionary();
        _writer.EndObject();

        PdfReference metadata = _writer.Allocate();
        _writer.BeginStreamObject(metadata);
        _writer.Name("Type"u8);
        _writer.Name("Metadata"u8);
        _writer.Name("Subtype"u8);
        _writer.Name("XML"u8);
        _writer.EndStreamObject(Xmp(document, options), compress: false); // Uncompressed, so any tool can read it.

        _writer.BeginObject(Catalog);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Catalog"u8);
        _writer.Name("Pages"u8);
        _writer.Reference(PageTree);
        _writer.Name("StructTreeRoot"u8);
        _writer.Reference(structTreeRoot);
        _writer.Name("MarkInfo"u8);
        _writer.BeginDictionary();
        _writer.Name("Marked"u8);
        _writer.Boolean(true);
        _writer.EndDictionary();
        _writer.Name("Lang"u8);
        _writer.TextString(document.Language);
        _writer.Name("ViewerPreferences"u8);
        _writer.BeginDictionary();
        _writer.Name("DisplayDocTitle"u8);
        _writer.Boolean(true);
        _writer.EndDictionary();
        _writer.Name("Metadata"u8);
        _writer.Reference(metadata);
        _writer.EndDictionary();
        _writer.EndObject();
        return info;
    }

    /// <summary>XMP with the same title, producer and dates as the Info dictionary, and the PDF/UA identifier when claimed.</summary>
    private static byte[] Xmp(PdfDocument document, RenderOptions options)
    {
        var xmp = new StringBuilder();
        xmp.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n")
            .Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n")
            .Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
        if (options.Conformance == PdfConformance.PdfUA1)
        {
            xmp.Append(" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"");
        }

        xmp.Append(">\n")
            .Append(CultureInfo.InvariantCulture, $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{SecurityElement.Escape(document.Title)}</rdf:li></rdf:Alt></dc:title>\n")
            .Append(CultureInfo.InvariantCulture, $"<dc:language><rdf:Bag><rdf:li>{SecurityElement.Escape(document.Language)}</rdf:li></rdf:Bag></dc:language>\n")
            .Append(CultureInfo.InvariantCulture, $"<pdf:Producer>{SecurityElement.Escape(Producer)}</pdf:Producer>\n");
        if (options.CreationDate is { } created)
        {
            string iso = created.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
            xmp.Append(CultureInfo.InvariantCulture, $"<xmp:CreateDate>{iso}</xmp:CreateDate>\n<xmp:ModifyDate>{iso}</xmp:ModifyDate>\n");
        }

        if (options.Conformance == PdfConformance.PdfUA1)
        {
            xmp.Append("<pdfuaid:part>1</pdfuaid:part>\n");
        }

        xmp.Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(xmp.ToString());
    }

    /// <summary>A PDF date string (ISO 32000-1, 7.9.4): <c>D:YYYYMMDDHHmmSS+HH'mm'</c>.</summary>
    internal static string PdfDate(DateTimeOffset date)
    {
        TimeSpan offset = date.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{date:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }
}
