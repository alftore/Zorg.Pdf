using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// <summary>"Zorg.Pdf 0.5.0": the same for every file a build writes, so output stays deterministic.</summary>
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
    /// Writes a finished page: its content stream, then the page object with its resources. Streams in
    /// <paramref name="under"/> are drawn before it and those in <paramref name="over"/> after it; they may be written
    /// later (the header and footer need the page count). <paramref name="state"/> is a graphics state the page uses
    /// (a watermark's opacity). <paramref name="structParents"/> is the page's parent-tree key.
    /// </summary>
    public void WritePage(
        int index,
        int structParents,
        ReadOnlySpan<byte> content,
        IEnumerable<(byte[] Name, PdfReference Font)> fonts,
        IReadOnlyCollection<(byte[] Name, PdfReference Image)> images,
        IReadOnlyList<PdfReference> under,
        IReadOnlyList<PdfReference> over,
        (byte[] Name, PdfReference State)? state = null,
        IReadOnlyList<PdfReference>? annotations = null)
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

        if (state is { } graphics)
        {
            _writer.Name("ExtGState"u8);
            _writer.BeginDictionary();
            _writer.Name(graphics.Name);
            _writer.Reference(graphics.State);
            _writer.EndDictionary();
        }

        _writer.EndDictionary();
        _writer.Name("Contents"u8);
        if (under.Count + over.Count > 0)
        {
            _writer.BeginArray();
            foreach (PdfReference before in under)
            {
                _writer.Reference(before);
            }

            _writer.Reference(stream);
            foreach (PdfReference after in over)
            {
                _writer.Reference(after);
            }

            _writer.EndArray();
        }
        else
        {
            _writer.Reference(stream);
        }

        if (annotations is { Count: > 0 })
        {
            _writer.Name("Annots"u8);
            _writer.BeginArray();
            foreach (PdfReference annotation in annotations)
            {
                _writer.Reference(annotation);
            }

            _writer.EndArray();
        }

        _writer.Name("StructParents"u8);
        _writer.Integer(structParents);
        _writer.Name("Tabs"u8);
        _writer.Name("S"u8);
        _writer.EndDictionary();
        _writer.EndObject();
    }

    /// <summary>
    /// Writes an invisible signature field: the field and its widget in one dictionary, on the first page, with a
    /// zero-size rectangle (ISO 32000-1, 12.7.4.5), its value the signature dictionary the writer finishes the file with.
    /// </summary>
    public void WriteSignatureField(PdfReference field, PdfReference signature, string name)
    {
        _writer.BeginObject(field);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Annot"u8);
        _writer.Name("Subtype"u8);
        _writer.Name("Widget"u8);
        _writer.Name("FT"u8);
        _writer.Name("Sig"u8);
        _writer.Name("T"u8);
        _writer.TextString(name);
        _writer.Name("V"u8);
        _writer.Reference(signature);
        _writer.Name("Rect"u8);
        _writer.BeginArray();
        _writer.Integer(0);
        _writer.Integer(0);
        _writer.Integer(0);
        _writer.Integer(0);
        _writer.EndArray();
        _writer.Name("F"u8);
        _writer.Integer(132); // Print (PDF/A requires it) and Locked.
        _writer.Name("P"u8);
        _writer.Reference(_pages[0]);
        _writer.EndDictionary();
        _writer.EndObject();
    }

    /// <summary>
    /// Writes a visible signature field: field and widget in one dictionary, with its appearance, a description for
    /// screen readers (/TU) and its parent-tree key. <paramref name="signature"/> is the signature dictionary of the
    /// field that's signed, or null for an empty one.
    /// </summary>
    public void WriteSignatureWidget(PdfReference widget, int page, string name, string label, (double Left, double Bottom, double Right, double Top) rect, PdfReference appearance, int structParent, PdfReference? signature)
    {
        _writer.BeginObject(widget);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Annot"u8);
        _writer.Name("Subtype"u8);
        _writer.Name("Widget"u8);
        _writer.Name("FT"u8);
        _writer.Name("Sig"u8);
        _writer.Name("T"u8);
        _writer.TextString(name);
        _writer.Name("TU"u8);
        _writer.TextString(label);
        if (signature is { } value)
        {
            _writer.Name("V"u8);
            _writer.Reference(value);
        }

        _writer.Name("Rect"u8);
        _writer.BeginArray();
        _writer.Real(rect.Left);
        _writer.Real(rect.Bottom);
        _writer.Real(rect.Right);
        _writer.Real(rect.Top);
        _writer.EndArray();
        _writer.Name("F"u8);
        _writer.Integer(signature is null ? 4 : 132); // Print (PDF/A requires it); a signed field is Locked too.
        _writer.Name("P"u8);
        _writer.Reference(_pages[page]);
        _writer.Name("AP"u8);
        _writer.BeginDictionary();
        _writer.Name("N"u8);
        _writer.Reference(appearance);
        _writer.EndDictionary();
        _writer.Name("StructParent"u8);
        _writer.Integer(structParent);
        _writer.EndDictionary();
        _writer.EndObject();
    }

    /// <summary>Writes the page tree, Info, XMP and catalog. Returns the Info dictionary for the trailer.</summary>
    public PdfReference Finish(PdfDocument document, RenderOptions options, PdfReference structTreeRoot, IReadOnlyList<OutlineEntry> outline, IReadOnlyDictionary<string, (int Page, double Top)> destinations, IReadOnlyList<PageSection> labels, IReadOnlyList<(Attachment Attachment, ReadOnlyMemory<byte> Bytes)> attachments, IReadOnlyList<PdfReference>? formFields = null, bool signed = false)
    {
        List<(byte[] Key, PdfReference Spec)> files = WriteAttachments(attachments, options);
        PdfReference? outlines = outline.Count > 0 ? WriteOutline(outline) : null;
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
        if (outlines is { } outlineRoot)
        {
            _writer.Name("Outlines"u8);
            _writer.Reference(outlineRoot);
            _writer.Name("PageMode"u8);
            _writer.Name("UseOutlines"u8);
        }

        if (labels.Count > 0)
        {
            // A number tree with one node: where each section starts and how it counts (ISO 32000-1, 12.4.2).
            _writer.Name("PageLabels"u8);
            _writer.BeginDictionary();
            _writer.Name("Nums"u8);
            _writer.BeginArray();
            foreach (PageSection section in labels)
            {
                _writer.Integer(section.FirstPage);
                _writer.BeginDictionary();
                _writer.Name("S"u8);
                _writer.Name(section.Style switch
                {
                    PageNumberStyle.LowerRoman => "r"u8,
                    PageNumberStyle.UpperRoman => "R"u8,
                    PageNumberStyle.LowerLetter => "a"u8,
                    PageNumberStyle.UpperLetter => "A"u8,
                    _ => "D"u8,
                });
                if (section.Start != 1)
                {
                    _writer.Name("St"u8);
                    _writer.Integer(section.Start);
                }

                if (section.Prefix is { } prefix)
                {
                    _writer.Name("P"u8);
                    _writer.TextString(prefix);
                }

                _writer.EndDictionary();
            }

            _writer.EndArray();
            _writer.EndDictionary();
        }

        if (destinations.Count > 0 || files.Count > 0)
        {
            _writer.Name("Names"u8);
            _writer.BeginDictionary();
            if (destinations.Count > 0)
            {
                // A name tree with one node: every destination, sorted by name as the tree requires (ISO 32000-1, 7.9.6).
                _writer.Name("Dests"u8);
                _writer.BeginDictionary();
                _writer.Name("Names"u8);
                _writer.BeginArray();
                foreach ((string name, (int page, double top)) in destinations.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    _writer.TextString(name);
                    Destination(page, top);
                }

                _writer.EndArray();
                _writer.EndDictionary();
            }

            if (files.Count > 0)
            {
                // The attachments panel: a name tree sorted by the keys' bytes.
                _writer.Name("EmbeddedFiles"u8);
                _writer.BeginDictionary();
                _writer.Name("Names"u8);
                _writer.BeginArray();
                foreach ((byte[] key, PdfReference spec) in files)
                {
                    _writer.HexString(key);
                    _writer.Reference(spec);
                }

                _writer.EndArray();
                _writer.EndDictionary();
            }

            _writer.EndDictionary();
        }

        if (formFields is { Count: > 0 })
        {
            _writer.Name("AcroForm"u8);
            _writer.BeginDictionary();
            _writer.Name("Fields"u8);
            _writer.BeginArray();
            foreach (PdfReference field in formFields)
            {
                _writer.Reference(field);
            }

            _writer.EndArray();
            if (signed)
            {
                // SigFlags 3: the document has signatures, and changes must be appended (ISO 32000-1, 12.7.2).
                _writer.Name("SigFlags"u8);
                _writer.Integer(3);
            }

            _writer.EndDictionary();
        }

        if (files.Count > 0)
        {
            // Associated files of the document (PDF/A-3, 6.8; ISO 32000-2, 14.13).
            _writer.Name("AF"u8);
            _writer.BeginArray();
            foreach ((_, PdfReference spec) in files)
            {
                _writer.Reference(spec);
            }

            _writer.EndArray();
        }

        if (options.Archive != PdfArchive.None)
        {
            // PDF/A: colour is defined by an sRGB output intent (ISO 19005-2, 6.2.3), the ICC's own profile.
            PdfReference profile = _writer.Allocate();
            _writer.Name("OutputIntents"u8);
            _writer.BeginArray();
            _writer.BeginDictionary();
            _writer.Name("Type"u8);
            _writer.Name("OutputIntent"u8);
            _writer.Name("S"u8);
            _writer.Name("GTS_PDFA1"u8);
            _writer.Name("OutputConditionIdentifier"u8);
            _writer.TextString("sRGB IEC61966-2.1");
            _writer.Name("RegistryName"u8);
            _writer.TextString("http://www.color.org");
            _writer.Name("Info"u8);
            _writer.TextString("sRGB IEC61966-2.1");
            _writer.Name("DestOutputProfile"u8);
            _writer.Reference(profile);
            _writer.EndDictionary();
            _writer.EndArray();
            _writer.EndDictionary();
            _writer.EndObject();

            _writer.BeginStreamObject(profile);
            _writer.Name("N"u8);
            _writer.Integer(3);
            _writer.EndStreamObject(SrgbProfile.Value, compress: true);
            return info;
        }

        _writer.EndDictionary();
        _writer.EndObject();
        return info;
    }

    /// <summary><c>[page /XYZ null top null]</c>: the page, scrolled so <paramref name="top"/> is at the top, at any zoom.</summary>
    private void Destination(int page, double top)
    {
        _writer.BeginArray();
        _writer.Reference(_pages[page]);
        _writer.Name("XYZ"u8);
        _writer.Null();
        _writer.Real(top);
        _writer.Null();
        _writer.EndArray();
    }

    /// <summary>
    /// The outline (bookmarks): one item per heading, nested by level, every item open (ISO 32000-1, 12.3.3). A level
    /// that skips nests under the nearest shallower heading.
    /// </summary>
    private PdfReference WriteOutline(IReadOnlyList<OutlineEntry> entries)
    {
        PdfReference root = _writer.Allocate();
        var references = new PdfReference[entries.Count];
        var parents = new int[entries.Count];
        var children = new List<int>[entries.Count + 1]; // Index entries.Count is the root.
        for (int i = 0; i <= entries.Count; i++)
        {
            children[i] = [];
        }

        var open = new Stack<int>();
        for (int i = 0; i < entries.Count; i++)
        {
            references[i] = _writer.Allocate();
            while (open.Count > 0 && entries[open.Peek()].Level >= entries[i].Level)
            {
                open.Pop();
            }

            parents[i] = open.Count > 0 ? open.Peek() : entries.Count;
            children[parents[i]].Add(i);
            open.Push(i);
        }

        int Descendants(int i) => children[i].Sum(c => 1 + Descendants(c));

        for (int i = 0; i < entries.Count; i++)
        {
            List<int> siblings = children[parents[i]];
            int at = siblings.IndexOf(i);
            _writer.BeginObject(references[i]);
            _writer.BeginDictionary();
            _writer.Name("Title"u8);
            _writer.TextString(entries[i].Title.Length > 0 ? entries[i].Title : "Untitled");
            _writer.Name("Parent"u8);
            _writer.Reference(parents[i] == entries.Count ? root : references[parents[i]]);
            if (at > 0)
            {
                _writer.Name("Prev"u8);
                _writer.Reference(references[siblings[at - 1]]);
            }

            if (at < siblings.Count - 1)
            {
                _writer.Name("Next"u8);
                _writer.Reference(references[siblings[at + 1]]);
            }

            if (children[i].Count > 0)
            {
                _writer.Name("First"u8);
                _writer.Reference(references[children[i][0]]);
                _writer.Name("Last"u8);
                _writer.Reference(references[children[i][^1]]);
                _writer.Name("Count"u8);
                _writer.Integer(Descendants(i));
            }

            _writer.Name("Dest"u8);
            Destination(entries[i].Page, entries[i].Top);
            _writer.EndDictionary();
            _writer.EndObject();
        }

        _writer.BeginObject(root);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("Outlines"u8);
        _writer.Name("First"u8);
        _writer.Reference(references[children[entries.Count][0]]);
        _writer.Name("Last"u8);
        _writer.Reference(references[children[entries.Count][^1]]);
        _writer.Name("Count"u8);
        _writer.Integer(entries.Count);
        _writer.EndDictionary();
        _writer.EndObject();
        return root;
    }

    /// <summary>The ICC's sRGB profile (sRGB2014.icc), embedded unchanged as PDF/A's output intent.</summary>
    private static readonly Lazy<byte[]> SrgbProfile = new(() =>
    {
        using Stream stream = typeof(DocumentWriter).Assembly.GetManifestResourceStream("sRGB2014.icc")
            ?? throw new InvalidOperationException("The sRGB profile resource is missing from the assembly.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    /// <summary>
    /// XMP with the same title, producer and dates as the Info dictionary, the PDF/UA identifier when claimed, and the
    /// PDF/A identifier when claimed. With both, PDF/A requires the pdfuaid schema to be declared as an extension.
    /// </summary>
    /// <summary>One schema in the PDF/A extension schema: its name, namespace, prefix and properties.</summary>
    private static void ExtensionSchema(StringBuilder xmp, string name, string namespaceUri, string prefix, (string Name, string ValueType, string Category, string Description)[] properties)
    {
        xmp.Append("<rdf:li rdf:parseType=\"Resource\">\n")
            .Append(CultureInfo.InvariantCulture, $"<pdfaSchema:schema>{SecurityElement.Escape(name)}</pdfaSchema:schema>\n")
            .Append(CultureInfo.InvariantCulture, $"<pdfaSchema:namespaceURI>{SecurityElement.Escape(namespaceUri)}</pdfaSchema:namespaceURI>\n")
            .Append(CultureInfo.InvariantCulture, $"<pdfaSchema:prefix>{prefix}</pdfaSchema:prefix>\n")
            .Append("<pdfaSchema:property><rdf:Seq>\n");
        foreach ((string property, string valueType, string category, string description) in properties)
        {
            xmp.Append("<rdf:li rdf:parseType=\"Resource\">\n")
                .Append(CultureInfo.InvariantCulture, $"<pdfaProperty:name>{property}</pdfaProperty:name><pdfaProperty:valueType>{valueType}</pdfaProperty:valueType><pdfaProperty:category>{category}</pdfaProperty:category>\n")
                .Append(CultureInfo.InvariantCulture, $"<pdfaProperty:description>{SecurityElement.Escape(description)}</pdfaProperty:description>\n")
                .Append("</rdf:li>\n");
        }

        xmp.Append("</rdf:Seq></pdfaSchema:property>\n</rdf:li>\n");
    }

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

        if (options.Archive != PdfArchive.None)
        {
            xmp.Append(" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"");
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

        if (options.Archive != PdfArchive.None)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"<pdfaid:part>{options.Archive.Part()}</pdfaid:part>\n<pdfaid:conformance>{options.Archive.Level()}</pdfaid:conformance>\n");
        }

        xmp.Append("</rdf:Description>\n");
        foreach (XmpSchema schema in options.MetadataSchemas)
        {
            xmp.Append(CultureInfo.InvariantCulture, $"<rdf:Description rdf:about=\"\" xmlns:{schema.Prefix}=\"{SecurityElement.Escape(schema.Namespace)}\">\n");
            foreach (XmpProperty property in schema.Properties)
            {
                xmp.Append(CultureInfo.InvariantCulture, $"<{schema.Prefix}:{property.Name}>{SecurityElement.Escape(property.Value)}</{schema.Prefix}:{property.Name}>\n");
            }

            xmp.Append("</rdf:Description>\n");
        }

        bool uaSchema = options.Conformance == PdfConformance.PdfUA1;
        if (options.Archive != PdfArchive.None && (uaSchema || !options.MetadataSchemas.IsEmpty))
        {
            // PDF/A accepts only predefined XMP schemas unless an extension schema describes the others (ISO 19005-2, 6.6.2.3).
            xmp.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n")
                .Append("<pdfaExtension:schemas><rdf:Bag>\n");
            if (uaSchema)
            {
                ExtensionSchema(xmp, "PDF/UA Universal Accessibility Schema", "http://www.aiim.org/pdfua/ns/id/", "pdfuaid", [("part", "Integer", "internal", "Indicates, which part of ISO 14289 standard is followed")]);
            }

            foreach (XmpSchema schema in options.MetadataSchemas)
            {
                ExtensionSchema(xmp, schema.Name, schema.Namespace, schema.Prefix, [.. schema.Properties.Select(p => (p.Name, "Text", p.Category, p.Description))]);
            }

            xmp.Append("</rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n");
        }

        xmp.Append("</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(xmp.ToString());
    }

    /// <summary>A PDF date string (ISO 32000-1, 7.9.4): <c>D:YYYYMMDDHHmmSS+HH'mm'</c>.</summary>
    /// <summary>
    /// Writes each attachment as an embedded file stream and its file specification, before the catalog: the name as
    /// /F (ASCII) and /UF (Unicode), the media type, size, MD5 checksum and relationship. Sorted by the name tree's keys.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "ISO 32000-1, 7.11.4: an embedded file's /CheckSum is MD5. It identifies, it doesn't protect.")]
    private List<(byte[] Key, PdfReference Spec)> WriteAttachments(IReadOnlyList<(Attachment Attachment, ReadOnlyMemory<byte> Bytes)> attachments, RenderOptions options)
    {
        var files = new List<(byte[] Key, PdfReference Spec)>();
        foreach ((Attachment attachment, ReadOnlyMemory<byte> bytes) in attachments)
        {
            PdfReference stream = _writer.Allocate(), spec = _writer.Allocate();
            _writer.BeginStreamObject(stream);
            _writer.Name("Type"u8);
            _writer.Name("EmbeddedFile"u8);
            _writer.Name("Subtype"u8);
            _writer.Name(Encoding.ASCII.GetBytes(attachment.MimeType));
            _writer.Name("Params"u8);
            _writer.BeginDictionary();
            _writer.Name("Size"u8);
            _writer.Integer(bytes.Length);
            _writer.Name("CheckSum"u8);
            _writer.HexString(System.Security.Cryptography.MD5.HashData(bytes.Span));
            if (options.CreationDate is { } created)
            {
                _writer.Name("ModDate"u8);
                _writer.TextString(PdfDate(created));
            }

            _writer.EndDictionary();
            _writer.EndStreamObject(bytes.Span, compress: true);

            _writer.BeginObject(spec);
            _writer.BeginDictionary();
            _writer.Name("Type"u8);
            _writer.Name("Filespec"u8);
            _writer.Name("F"u8);
            _writer.TextString(new string([.. attachment.Name.Select(c => c is >= ' ' and <= '~' ? c : '_')]));
            _writer.Name("UF"u8);
            _writer.TextString(attachment.Name);
            if (attachment.Description is { Length: > 0 } description)
            {
                _writer.Name("Desc"u8);
                _writer.TextString(description);
            }

            _writer.Name("AFRelationship"u8);
            _writer.Name(attachment.Relationship switch
            {
                AttachmentRelationship.Source => "Source"u8,
                AttachmentRelationship.Data => "Data"u8,
                AttachmentRelationship.Alternative => "Alternative"u8,
                AttachmentRelationship.Supplement => "Supplement"u8,
                _ => "Unspecified"u8,
            });
            _writer.Name("EF"u8);
            _writer.BeginDictionary();
            _writer.Name("F"u8);
            _writer.Reference(stream);
            _writer.Name("UF"u8);
            _writer.Reference(stream);
            _writer.EndDictionary();
            _writer.EndDictionary();
            _writer.EndObject();
            files.Add((Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(attachment.Name)).ToArray(), spec));
        }

        files.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        return files;
    }

    internal static string PdfDate(DateTimeOffset date)
    {
        TimeSpan offset = date.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{date:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }
}

/// <summary>A heading in the outline: its text, level, page index and top in points from the page's bottom.</summary>
internal readonly record struct OutlineEntry(string Title, int Level, int Page, double Top);
