using System.Collections.Generic;
using System.Linq;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>Standard structure types (ISO 32000-1, 14.8.4). An enum, so a typo can't reach the file.</summary>
internal enum StructureRole
{
    Document,
    H1,
    H2,
    H3,
    H4,
    H5,
    H6,
    P,
}

/// <summary>
/// Builds the structure tree that makes the output accessible: elements in reading order, marked content linked to
/// them by MCID, and the parent tree that maps each page's marked content back. Elements are written as soon as
/// they're complete, so a long document never holds its whole tree in memory; only references are kept.
/// </summary>
internal sealed class Tagger
{
    private static readonly byte[][] RoleNames =
        [.. new[] { "Document", "H1", "H2", "H3", "H4", "H5", "H6", "P" }.Select(name => System.Text.Encoding.ASCII.GetBytes(name))];

    private readonly PdfObjectWriter _writer;
    private readonly List<List<PdfReference>> _parentTree = []; // Per page, the element that owns each MCID.
    private readonly Element _root;

    public Tagger(PdfObjectWriter writer)
    {
        _writer = writer;
        StructTreeRoot = writer.Allocate();
        _root = new Element(StructureRole.Document, writer.Allocate(), StructTreeRoot);
    }

    public PdfReference StructTreeRoot { get; }

    public static StructureRole Heading(int level) => StructureRole.H1 + (System.Math.Clamp(level, 1, 6) - 1);

    public static byte[] Name(StructureRole role) => RoleNames[(int)role];

    /// <summary>Starts a page: its marked content gets MCIDs from 0.</summary>
    public void StartPage() => _parentTree.Add([]);

    /// <summary>A new element under the document element, in reading order.</summary>
    public Element Begin(StructureRole role)
    {
        var element = new Element(role, _writer.Allocate(), _root.Reference);
        _root.Kids.Add(element.Reference);
        return element;
    }

    /// <summary>The next MCID on the page, owned by <paramref name="element"/>.</summary>
    public int Mark(Element element, int page)
    {
        List<PdfReference> owners = _parentTree[page];
        element.Kids.Add(new MarkedContent(page, owners.Count));
        owners.Add(element.Reference);
        return owners.Count - 1;
    }

    /// <summary>Writes a finished element; after this only its reference is kept.</summary>
    public void End(Element element, IReadOnlyList<PdfReference> pages) => Write(element, pages);

    /// <summary>Writes the document element, the structure tree root and the parent tree.</summary>
    public void Finish(IReadOnlyList<PdfReference> pages)
    {
        Write(_root, pages);
        PdfReference parentTree = _writer.Allocate();

        _writer.BeginObject(StructTreeRoot);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("StructTreeRoot"u8);
        _writer.Name("K"u8);
        _writer.Reference(_root.Reference);
        _writer.Name("ParentTree"u8);
        _writer.Reference(parentTree);
        _writer.Name("ParentTreeNextKey"u8);
        _writer.Integer(_parentTree.Count);
        _writer.EndDictionary();
        _writer.EndObject();

        _writer.BeginObject(parentTree);
        _writer.BeginDictionary();
        _writer.Name("Nums"u8);
        _writer.BeginArray();
        for (int page = 0; page < _parentTree.Count; page++)
        {
            _writer.Integer(page);
            _writer.BeginArray();
            foreach (PdfReference owner in _parentTree[page])
            {
                _writer.Reference(owner);
            }

            _writer.EndArray();
        }

        _writer.EndArray();
        _writer.EndDictionary();
        _writer.EndObject();
    }

    private void Write(Element element, IReadOnlyList<PdfReference> pages)
    {
        _writer.BeginObject(element.Reference);
        _writer.BeginDictionary();
        _writer.Name("Type"u8);
        _writer.Name("StructElem"u8);
        _writer.Name("S"u8);
        _writer.Name(Name(element.Role));
        _writer.Name("P"u8);
        _writer.Reference(element.Parent);

        // Marked content on one page names the page once (/Pg); content on several pages needs MCR dictionaries.
        int firstPage = -1;
        bool onePage = true;
        foreach (object kid in element.Kids)
        {
            if (kid is MarkedContent mark)
            {
                onePage &= firstPage < 0 || firstPage == mark.Page;
                firstPage = firstPage < 0 ? mark.Page : firstPage;
            }
        }

        if (firstPage >= 0 && onePage)
        {
            _writer.Name("Pg"u8);
            _writer.Reference(pages[firstPage]);
        }

        _writer.Name("K"u8);
        _writer.BeginArray();
        foreach (object kid in element.Kids)
        {
            switch (kid)
            {
                case PdfReference child:
                    _writer.Reference(child);
                    break;
                case MarkedContent mark when onePage:
                    _writer.Integer(mark.Mcid);
                    break;
                case MarkedContent mark:
                    _writer.BeginDictionary();
                    _writer.Name("Type"u8);
                    _writer.Name("MCR"u8);
                    _writer.Name("Pg"u8);
                    _writer.Reference(pages[mark.Page]);
                    _writer.Name("MCID"u8);
                    _writer.Integer(mark.Mcid);
                    _writer.EndDictionary();
                    break;
            }
        }

        _writer.EndArray();
        _writer.EndDictionary();
        _writer.EndObject();
        element.Kids.Clear();
    }

    internal readonly record struct MarkedContent(int Page, int Mcid);

    /// <summary>A structure element. Its kids are child references and marked content, in reading order.</summary>
    internal sealed class Element(StructureRole role, PdfReference reference, PdfReference parent)
    {
        public StructureRole Role { get; } = role;

        public PdfReference Reference { get; } = reference;

        public PdfReference Parent { get; } = parent;

        public List<object> Kids { get; } = [];
    }
}
