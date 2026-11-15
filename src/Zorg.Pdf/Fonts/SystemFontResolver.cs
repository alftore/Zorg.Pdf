using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// Finds installed TrueType fonts by family name. The index is built once per process from each file's table
/// directory, <c>name</c> (IDs 1, 2, 16), <c>OS/2</c> (weight, fsSelection) and <c>head</c> (macStyle), read with
/// positional reads so a folder of large fonts costs a few kilobytes per file. Files that aren't plain TrueType
/// (CFF outlines) are skipped, because the renderer can't embed them; each TrueType face of a collection (.ttc) is indexed.
/// </summary>
internal sealed class SystemFontResolver : IFontResolver
{
    public static readonly SystemFontResolver Instance = new();

    private readonly Lazy<Dictionary<string, List<(string Path, bool Bold, bool Italic, int Face, int Width)>>> _index = new(Build);

    public ValueTask<FontSource?> ResolveAsync(FontRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Find(request));

    internal FontSource? Find(FontRequest request)
    {
        if (!_index.Value.TryGetValue(request.Family, out List<(string Path, bool Bold, bool Italic, int Face, int Width)>? faces))
        {
            return null;
        }

        bool bold = request.Weight == FontWeight.Bold;
        bool italic = request.Style == FontStyle.Italic;

        // Exact face first, then same weight, then the regular face, then anything. Ties go to normal width (a family's
        // condensed faces, such as Arial Narrow under "Arial", come after), then to the path in ordinal order.
        (string Path, bool Bold, bool Italic, int Face, int Width) best = faces
            .OrderBy(f => (f.Bold == bold ? 0 : 2) + (f.Italic == italic ? 0 : 1))
            .ThenBy(f => Math.Abs(f.Width - 5))
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Face)
            .First();
        return FontSource.FromFile(best.Path, best.Face);
    }

    internal static IEnumerable<string> Folders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".local", "share", "fonts");
            yield return Path.Combine(home, ".fonts");
        }
    }

    private static Dictionary<string, List<(string Path, bool Bold, bool Italic, int Face, int Width)>> Build()
    {
        var index = new Dictionary<string, List<(string, bool, bool, int, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Folders().Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                foreach ((string family, bool bold, bool italic, _, int face, int width) in DescribeFaces(file))
                {
                    if (!index.TryGetValue(family, out List<(string, bool, bool, int, int)>? faces))
                    {
                        index[family] = faces = [];
                    }

                    faces.Add((file, bold, italic, face, width));
                }
            }
        }

        return index;
    }

    /// <summary>The family, face and weight class of a font file's first face; a null family when it isn't a TrueType font this library can embed.</summary>
    internal static (string? Family, bool Bold, bool Italic, int Weight) Describe(string path)
    {
        foreach ((string family, bool bold, bool italic, int weight, int face, _) in DescribeFaces(path))
        {
            if (face == 0)
            {
                return (family, bold, italic, weight);
            }
        }

        return default;
    }

    /// <summary>
    /// Every TrueType face in a file: one for a <c>.ttf</c>, each face of a <c>.ttc</c> collection with its index.
    /// Faces with CFF outlines, and files that aren't fonts, give nothing.
    /// </summary>
    internal static List<(string Family, bool Bold, bool Italic, int Weight, int Face, int Width)> DescribeFaces(string path)
    {
        var found = new List<(string, bool, bool, int, int, int)>();
        try
        {
            using SafeFileHandle file = File.OpenHandle(path);
            long length = RandomAccess.GetLength(file);
            Span<byte> header = stackalloc byte[12];
            if (length < 12 || RandomAccess.Read(file, header, 0) != 12)
            {
                return found;
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0x74746366) // 'ttcf'
            {
                int faces = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header[8..]), 256);
                byte[] offsets = new byte[faces * 4];
                if (RandomAccess.Read(file, offsets, 12) != offsets.Length)
                {
                    return found;
                }

                for (int face = 0; face < faces; face++)
                {
                    if (DescribeAt(file, length, BinaryPrimitives.ReadUInt32BigEndian(offsets.AsSpan(face * 4))) is ({ } family, bool bold, bool italic, int weight, int width))
                    {
                        found.Add((family, bold, italic, weight, face, width));
                    }
                }
            }
            else if (DescribeAt(file, length, 0) is ({ } family, bool bold, bool italic, int weight, int width))
            {
                found.Add((family, bold, italic, weight, 0, width));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not readable: no faces.
        }

        return found;
    }

    /// <summary>One face's family, style and weight, from the table directory at <paramref name="start"/>.</summary>
    private static (string? Family, bool Bold, bool Italic, int Weight, int Width) DescribeAt(SafeFileHandle file, long length, long start)
    {
        Span<byte> header = stackalloc byte[12];
        if (start + 12 > length || RandomAccess.Read(file, header, start) != 12 || BinaryPrimitives.ReadUInt32BigEndian(header) is not (0x00010000 or 0x74727565))
        {
            return default;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        byte[] directory = new byte[count * 16];
        if (count is 0 or > 512 || RandomAccess.Read(file, directory, start + 12) != directory.Length)
        {
            return default;
        }

        byte[]? name = null;
        byte[]? os2 = null;
        byte[]? head = null;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = directory.AsSpan(i * 16, 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(entry[12..]), 1 << 20);
            if (offset + size > length || tag is not (0x6E616D65 or 0x4F532F32 or 0x68656164))
            {
                continue;
            }

            byte[] table = new byte[size];
            if (RandomAccess.Read(file, table, offset) != size)
            {
                return default;
            }

            switch (tag)
            {
                case 0x6E616D65: // name
                    name = table;
                    break;
                case 0x4F532F32: // OS/2
                    os2 = table;
                    break;
                case 0x68656164: // head
                    head = table;
                    break;
            }
        }

        string? family = name is null ? null : FamilyName(name);
        ushort macStyle = head is { Length: >= 46 } ? BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(44)) : (ushort)0;
        int weight = os2 is { Length: >= 6 } ? BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4)) : 400;
        ushort selection = os2 is { Length: >= 64 } ? BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62)) : (ushort)0;
        int width = os2 is { Length: >= 8 } ? BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(6)) : 5; // usWidthClass: 5 is normal.
        return (family, weight >= 600 || (macStyle & 1) != 0, (selection & 1) != 0 || (macStyle & 2) != 0, weight, width);
    }

    /// <summary>The typographic family (name ID 16), else the family (ID 1), preferring Windows English.</summary>
    private static string? FamilyName(byte[] name)
    {
        if (name.Length < 6)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
        int strings = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
        string? typographic = null;
        string? family = null;
        for (int i = 0; i < count && 6 + ((i + 1) * 12) <= name.Length; i++)
        {
            ReadOnlySpan<byte> record = name.AsSpan(6 + (i * 12), 12);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            int language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
            int id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            int offset = strings + BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (id is not (1 or 16) || offset + length > name.Length)
            {
                continue;
            }

            string? text = platform switch
            {
                3 when language == 0x409 => Encoding.BigEndianUnicode.GetString(name, offset, length),
                1 => Encoding.Latin1.GetString(name, offset, length),
                _ => null,
            };
            if (text is null)
            {
                continue;
            }

            if (id == 16)
            {
                typographic ??= text;
            }
            else
            {
                family ??= text;
            }
        }

        return typographic ?? family;
    }
}
