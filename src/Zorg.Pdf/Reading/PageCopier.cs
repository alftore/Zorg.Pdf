using System;
using System.Collections.Generic;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Reading;

/// <summary>
/// Copies pages of one file into the file being written: each page's dictionary with its inherited attributes made
/// explicit, its content and resources, and its annotations. Links to copied pages are kept (named destinations made
/// explicit); links to pages left out, and scripts, are dropped. A page selected twice is copied twice.
/// </summary>
internal sealed class PageCopier(CosFile file, PdfObjectWriter writer, PdfReadLimits limits, PdfFiles.Notes notes, string path)
{
    // Rewritten here, or tied to the old document: its tree, tags, thumbnails, articles, scripts, and its own data.
    private static readonly HashSet<string> PageSkipped = new(StringComparer.Ordinal)
    {
        "Type", "Parent", "MediaBox", "CropBox", "Rotate", "Resources", "Annots", "StructParents", "Thumb", "B", "AA", "PieceInfo", "Metadata", "Tabs",
    };

    private static readonly HashSet<string> LinkSkipped = new(StringComparer.Ordinal) { "Dest", "A", "PA" };

    private readonly CosImporter _importer = new(file, writer, limits.MaxObjects);
    private Dictionary<string, CosValue>? _names;

    /// <summary>Copies the selected pages (from 0) and returns their new references, in order.</summary>
    public List<PdfReference> Copy(List<CosPage> pages, int[] selected, PdfReference parent)
    {
        // A page's first copy is where links to it point; pages not selected are left out.
        var targets = new Dictionary<int, PdfReference>();
        var references = new List<PdfReference>(selected.Length);
        foreach (int index in selected)
        {
            PdfReference reference = writer.Allocate();
            references.Add(reference);
            if (pages[index].ObjectNumber >= 0 && targets.TryAdd(pages[index].ObjectNumber, reference))
            {
                _importer.Map(pages[index].ObjectNumber, reference);
            }
        }

        foreach (CosPage page in pages)
        {
            if (page.ObjectNumber >= 0 && !targets.ContainsKey(page.ObjectNumber))
            {
                _importer.Exclude(page.ObjectNumber);
            }
        }

        for (int i = 0; i < selected.Length; i++)
        {
            WritePage(pages[selected[i]], references[i], parent);
            _importer.Flush();
        }

        return references;
    }

    private void WritePage(CosPage page, PdfReference reference, PdfReference parent)
    {
        writer.BeginObject(reference);
        writer.BeginDictionary();
        writer.Name("Type"u8);
        writer.Name("Page"u8);
        writer.Name("Parent"u8);
        writer.Reference(parent);
        writer.Name("MediaBox"u8);
        Box(page.MediaBox);
        if (!page.CropBox.AsSpan().SequenceEqual(page.MediaBox))
        {
            writer.Name("CropBox"u8);
            Box(page.CropBox);
        }

        if (page.Rotate != 0)
        {
            writer.Name("Rotate"u8);
            writer.Integer(page.Rotate);
        }

        writer.Name("Resources"u8);
        if (page.Resources is { } resources && file.Resolve(resources) is CosDictionary)
        {
            _importer.Write(resources);
        }
        else
        {
            writer.BeginDictionary();
            writer.EndDictionary();
        }

        List<CosDictionary> annotations = Annotations(page);
        if (annotations.Count > 0)
        {
            writer.Name("Annots"u8);
            writer.BeginArray();
            foreach (CosDictionary annotation in annotations)
            {
                Annotation(annotation);
            }

            writer.EndArray();
        }

        _importer.Entries(page.Dictionary, PageSkipped);
        writer.EndDictionary();
        writer.EndObject();
    }

    private void Box(double[] box)
    {
        writer.BeginArray();
        foreach (double v in box)
        {
            writer.Real(v);
        }

        writer.EndArray();
    }

    /// <summary>The annotations to keep: links resolve to a copied page or a URI, nothing runs a script.</summary>
    private List<CosDictionary> Annotations(CosPage page)
    {
        var kept = new List<CosDictionary>();
        if (file.Resolve(page.Dictionary["Annots"]) is not CosArray array)
        {
            return kept;
        }

        foreach (CosValue item in array.Items)
        {
            if (file.Resolve(item) is not CosDictionary annotation)
            {
                continue;
            }

            string subtype = file.Resolve(annotation["Subtype"]) is CosName s ? s.Value : "";
            if (subtype == "Popup")
            {
                continue; // Belongs to another annotation, by /Parent, which isn't kept.
            }

            if (subtype == "Widget")
            {
                notes.Add(DiagnosticCodes.FilesFormFields, "Form fields are copied as they look: they can't be filled in or submitted in the new file.", path);
            }

            if (file.Resolve(annotation["A"]) is CosDictionary action && file.Resolve(action["S"]) is CosName { Value: not ("GoTo" or "URI") })
            {
                notes.Add(DiagnosticCodes.FilesLinksDropped, "Links that run scripts, open other files or launch programs aren't kept.", path);
                continue;
            }

            if (subtype == "Link" && Destination(annotation) is { } destination && !Copied(destination))
            {
                notes.Add(DiagnosticCodes.FilesLinksDropped, "Links to pages that weren't copied aren't kept.", path);
                continue;
            }

            kept.Add(annotation);
        }

        return kept;
    }

    private void Annotation(CosDictionary annotation)
    {
        writer.BeginDictionary();
        bool link = file.Resolve(annotation["Subtype"]) is CosName { Value: "Link" };
        CosArray? destination = link ? Destination(annotation) : null;
        _importer.Entries(annotation, destination is not null ? LinkSkipped : EmptySet);
        if (destination is not null)
        {
            writer.Name("Dest"u8);
            _importer.Write(destination);
        }

        writer.EndDictionary();
    }

    private static readonly HashSet<string> EmptySet = [];

    /// <summary>A link's explicit destination ([page /XYZ …]), named destinations looked up; null for a URI or none.</summary>
    private CosArray? Destination(CosDictionary annotation)
    {
        CosValue? target = annotation["Dest"];
        if (target is null && file.Resolve(annotation["A"]) is CosDictionary action && file.Resolve(action["S"]) is CosName { Value: "GoTo" })
        {
            target = action["D"];
        }

        CosValue resolved = file.Resolve(target);
        if (resolved is CosName or CosString)
        {
            resolved = file.Resolve(Named(resolved));
        }

        return resolved switch
        {
            CosArray { Items.Count: > 0 } explicitDestination => explicitDestination,
            CosDictionary { } wrapped when file.Resolve(wrapped["D"]) is CosArray { Items.Count: > 0 } d => d,
            _ => null,
        };
    }

    private bool Copied(CosArray destination) =>
        destination.Items[0] is CosReference page && !_importer.IsExcluded(page.Number);

    /// <summary>A named destination from the catalog's /Dests dictionary or /Names /Dests name tree.</summary>
    private CosValue? Named(CosValue name)
    {
        if (_names is null)
        {
            _names = new(StringComparer.Ordinal);
            if (file.Resolve(file.Catalog["Dests"]) is CosDictionary dests)
            {
                foreach ((string key, CosValue value) in dests.Entries)
                {
                    _names.TryAdd(key, value);
                }
            }

            if (file.Resolve(file.Catalog["Names"]) is CosDictionary names && file.Resolve(names["Dests"]) is CosDictionary tree)
            {
                var stack = new Stack<(CosDictionary Node, int Depth)>();
                stack.Push((tree, 0));
                var seen = new HashSet<CosDictionary>(ReferenceEqualityComparer.Instance);
                while (stack.Count > 0 && _names.Count < limits.MaxObjects)
                {
                    (CosDictionary node, int depth) = stack.Pop();
                    if (!seen.Add(node) || depth > CosLexer.MaxDepth)
                    {
                        continue;
                    }

                    if (file.Resolve(node["Names"]) is CosArray pairs)
                    {
                        for (int i = 0; i + 1 < pairs.Items.Count; i += 2)
                        {
                            if (file.Resolve(pairs.Items[i]) is CosString key)
                            {
                                _names.TryAdd(System.Text.Encoding.Latin1.GetString(key.Value), pairs.Items[i + 1]);
                            }
                        }
                    }

                    if (file.Resolve(node["Kids"]) is CosArray kids)
                    {
                        foreach (CosValue kid in kids.Items)
                        {
                            if (file.Resolve(kid) is CosDictionary child)
                            {
                                stack.Push((child, depth + 1));
                            }
                        }
                    }
                }
            }
        }

        string text = name switch
        {
            CosName n => n.Value,
            CosString s => System.Text.Encoding.Latin1.GetString(s.Value),
            _ => "",
        };
        return _names.TryGetValue(text, out CosValue? found) ? found : null;
    }
}
