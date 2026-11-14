using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Link annotations: each link gets one annotation per page it's on, with a rectangle per line of its text, a
/// <c>/StructParent</c> key and an <c>OBJR</c> under its Link element, so screen readers find it in reading order
/// (PDF/UA-1, 7.18.5). Annotations are written when their page is.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>The links on the current page, by their Link element, in the order they appear.</summary>
    private readonly List<PendingLink> _pageLinks = [];

    /// <summary>Adds the area of a run of link text on the current page, starting the page's annotation for it if needed.</summary>
    private void LinkArea(Link link, Tagger.Element element, double x, double y, double width, double height)
    {
        PendingLink? pending = _pageLinks.Find(p => ReferenceEquals(p.Element, element));
        if (pending is null)
        {
            PdfReference annotation = _writer.Allocate();
            int key = _tagger.ObjectKey(element);
            Tagger.AddObject(element, annotation, _page);
            pending = new PendingLink(element, link, annotation, key);
            _pageLinks.Add(pending);
        }

        // Runs of one line touch: widen the last area instead of adding a second one.
        if (pending.Areas.Count > 0 && pending.Areas[^1] is var last && Math.Abs(last.Y - y) < 0.01 && Math.Abs(last.X + last.Width - x) < 0.5)
        {
            pending.Areas[^1] = last with { Width = x + width - last.X };
        }
        else
        {
            pending.Areas.Add((x, y, width, height));
        }
    }

    /// <summary>Writes the current page's link annotations and returns their references for its <c>/Annots</c>.</summary>
    private PdfReference[] WriteLinkAnnotations()
    {
        var references = new PdfReference[_pageLinks.Count];
        for (int i = 0; i < _pageLinks.Count; i++)
        {
            PendingLink pending = _pageLinks[i];
            double left = pending.Areas.Min(a => a.X), bottom = pending.Areas.Min(a => a.Y);
            double right = pending.Areas.Max(a => a.X + a.Width), top = pending.Areas.Max(a => a.Y + a.Height);
            _writer.BeginObject(pending.Annotation);
            _writer.BeginDictionary();
            _writer.Name("Type"u8);
            _writer.Name("Annot"u8);
            _writer.Name("Subtype"u8);
            _writer.Name("Link"u8);
            _writer.Name("Rect"u8);
            Box(left, bottom, right, top);
            _writer.Name("QuadPoints"u8);
            _writer.BeginArray();
            foreach ((double x, double y, double width, double height) in pending.Areas)
            {
                // Each line's area, counterclockwise from its bottom left (as viewers read QuadPoints in practice).
                foreach (double value in (double[])[x, y, x + width, y, x + width, y + height, x, y + height])
                {
                    _writer.Real(value);
                }
            }

            _writer.EndArray();
            _writer.Name("Border"u8);
            _writer.BeginArray();
            _writer.Integer(0);
            _writer.Integer(0);
            _writer.Integer(0);
            _writer.EndArray();
            _writer.Name("F"u8);
            _writer.Integer(4); // Print: PDF/A requires it.
            _writer.Name("StructParent"u8);
            _writer.Integer(pending.Key);
            _writer.Name("Contents"u8);
            _writer.TextString(LinkDescription(pending.Link));
            if (pending.Link.Target.StartsWith('#'))
            {
                _writer.Name("Dest"u8);
                _writer.TextString(pending.Link.Target[1..]); // A named destination, written when the document finishes.
            }
            else
            {
                _writer.Name("A"u8);
                _writer.BeginDictionary();
                _writer.Name("S"u8);
                _writer.Name("URI"u8);
                _writer.Name("URI"u8);
                _writer.LiteralString(Encoding.ASCII.GetBytes(new Uri(pending.Link.Target).AbsoluteUri)); // URIs are 7-bit ASCII.
                _writer.EndDictionary();
            }

            _writer.EndDictionary();
            _writer.EndObject();
            references[i] = pending.Annotation;
        }

        _pageLinks.Clear();
        return references;
    }

    /// <summary>What the link does, for <c>/Contents</c>: its description, else its text.</summary>
    private static string LinkDescription(Link link) => link.Description ?? OutlineTitle(link.Inlines);

    private void Box(double left, double bottom, double right, double top)
    {
        _writer.BeginArray();
        _writer.Real(left);
        _writer.Real(bottom);
        _writer.Real(right);
        _writer.Real(top);
        _writer.EndArray();
    }

    /// <summary>A link's annotation on the current page, filled as its runs are drawn.</summary>
    private sealed class PendingLink(Tagger.Element element, Link link, PdfReference annotation, int key)
    {
        public Tagger.Element Element { get; } = element;

        public Link Link { get; } = link;

        public PdfReference Annotation { get; } = annotation;

        public int Key { get; } = key;

        public List<(double X, double Y, double Width, double Height)> Areas { get; } = [];
    }
}
