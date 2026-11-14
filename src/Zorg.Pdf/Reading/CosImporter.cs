using System;
using System.Collections.Generic;
using System.Text;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Reading;

/// <summary>
/// Copies objects from an existing file into the one being written, renumbered, each once. Streams are copied as
/// stored, filters and all. Keys that tie an object to its old document (structure, optional content, page parents,
/// metadata) are left out, so the copy stands alone.
/// </summary>
internal sealed class CosImporter(CosFile file, PdfObjectWriter writer, int maxObjects)
{
    // StructParent(s): the old parent tree. OC: optional content groups the new catalog doesn't list. Parent and P:
    // the old page tree and pages. Metadata and PieceInfo: the old application's data, not needed to draw.
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "StructParent", "StructParents", "OC", "Parent", "P", "Metadata", "PieceInfo" };

    private readonly Dictionary<int, PdfReference> _map = [];
    private readonly Queue<int> _pending = new();
    private readonly HashSet<int> _excluded = [];

    /// <summary>An object written by the caller (a page), so references to it point at its new number without copying it.</summary>
    public void Map(int number, PdfReference reference) => _map[number] = reference;

    /// <summary>An object never copied (a page left out): references to it are written as null.</summary>
    public void Exclude(int number) => _excluded.Add(number);

    /// <summary>Whether an object is left out.</summary>
    public bool IsExcluded(int number) => _excluded.Contains(number);

    /// <summary>The new reference for an old object, copied when <see cref="Flush"/> runs.</summary>
    public PdfReference Import(int number)
    {
        if (!_map.TryGetValue(number, out PdfReference reference))
        {
            if (_map.Count >= maxObjects)
            {
                throw new PdfReadException($"The background needs more than {maxObjects:N0} objects (PdfReadLimits.MaxObjects).");
            }

            reference = writer.Allocate();
            _map[number] = reference;
            _pending.Enqueue(number);
        }

        return reference;
    }

    /// <summary>Writes a value, importing what it refers to.</summary>
    public void Write(CosValue value)
    {
        switch (value)
        {
            case CosNull:
                writer.Null();
                break;
            case CosBoolean b:
                writer.Boolean(b.Value);
                break;
            case CosInteger i:
                writer.Integer(i.Value);
                break;
            case CosReal r:
                writer.Real(r.Value);
                break;
            case CosName n:
                writer.Name(Encoding.Latin1.GetBytes(n.Value));
                break;
            case CosString s when s.Hex:
                writer.HexString(s.Value);
                break;
            case CosString s:
                writer.LiteralString(s.Value);
                break;
            case CosArray a:
                writer.BeginArray();
                foreach (CosValue item in a.Items)
                {
                    Write(item);
                }

                writer.EndArray();
                break;
            case CosDictionary d:
                writer.BeginDictionary();
                Entries(d, skipLength: false);
                writer.EndDictionary();
                break;
            case CosReference reference when _excluded.Contains(reference.Number):
                writer.Null();
                break;
            case CosReference reference:
                writer.Reference(Import(reference.Number));
                break;
            default:
                writer.Null(); // A stream can only be indirect, and operators aren't values.
                break;
        }
    }

    /// <summary>Copies every object imported so far, and those they refer to.</summary>
    public void Flush()
    {
        while (_pending.Count > 0)
        {
            int number = _pending.Dequeue();
            PdfReference reference = _map[number];
            switch (file.Object(number))
            {
                case CosStream stream:
                    writer.BeginStreamObject(reference);
                    Entries(stream.Dictionary, skipLength: true);
                    writer.EndStreamObject(stream.Raw.Span, compress: false);
                    break;
                case var value:
                    writer.BeginObject(reference);
                    Write(value);
                    writer.EndObject();
                    break;
            }
        }
    }

    /// <summary>Writes a dictionary's entries (without the brackets), leaving out the keys named and the usual ones.</summary>
    public void Entries(CosDictionary dictionary, IReadOnlySet<string> skip)
    {
        var keys = new List<string>(dictionary.Entries.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (!Dropped.Contains(key) && !skip.Contains(key))
            {
                writer.Name(Encoding.Latin1.GetBytes(key));
                Write(dictionary.Entries[key]);
            }
        }
    }

    private void Entries(CosDictionary dictionary, bool skipLength)
    {
        var keys = new List<string>(dictionary.Entries.Keys);
        keys.Sort(StringComparer.Ordinal); // Deterministic output.
        foreach (string key in keys)
        {
            if (Dropped.Contains(key) || (skipLength && key == "Length"))
            {
                continue;
            }

            writer.Name(Encoding.Latin1.GetBytes(key));
            Write(dictionary.Entries[key]);
        }
    }
}
