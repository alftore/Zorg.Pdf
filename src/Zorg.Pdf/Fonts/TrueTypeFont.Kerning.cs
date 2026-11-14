using System;
using System.Collections.Generic;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// Pair kerning, from the OpenType <c>GPOS</c> table (pair adjustment lookups under the <c>kern</c> feature) or, for
/// fonts without one, the older <c>kern</c> table (format 0).
/// </summary>
/// <remarks>
/// A simplification for the spike: every lookup under any <c>kern</c> feature is used, whatever the script and
/// language. That's right for Latin, Greek and Cyrillic fonts, which register the same lookups for every script.
/// Only the horizontal advance of the first glyph is applied (XAdvance of value record 1), as browsers do for
/// horizontal Latin text.
/// </remarks>
internal sealed partial class TrueTypeFont
{
    /// <summary>The most pair-adjustment subtables read. Real fonts have tens; this bounds the work for a hostile one.</summary>
    internal const int MaxKerningSubtables = 4096;

    private const int GposPairAdjustment = 2;
    private const int GposExtension = 9;

    // Per lookup, the offsets (from the start of GPOS) of its pair adjustment subtables, in order.
    private readonly List<int[]> _pairLookups = [];
    private readonly Dictionary<uint, short> _kerningCache = [];
    private int _kernPairs; // Pairs in the legacy kern table, when that's the source.
    private int _kernPairsOffset = -1; // Offset of the first pair in the 'kern' table.

    /// <summary>Where the kerning comes from: "GPOS", "kern" or "none".</summary>
    public string KerningSource => _pairLookups.Count > 0 ? "GPOS" : _kernPairsOffset >= 0 ? "kern" : "none";

    /// <summary>The adjustment to the advance of <paramref name="left"/> when followed by <paramref name="right"/>, in font units. Usually negative.</summary>
    public int Kerning(ushort left, ushort right)
    {
        uint key = ((uint)left << 16) | right;
        if (_kerningCache.TryGetValue(key, out short cached))
        {
            return cached;
        }

        int value = _pairLookups.Count > 0 ? GposKerning(left, right) : KernTableKerning(left, right);
        short clamped = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        if (_kerningCache.Count < 65536)
        {
            _kerningCache[key] = clamped;
        }

        return clamped;
    }

    /// <summary>The value from the legacy kern table alone, for tests that compare the two sources.</summary>
    internal int KernTableKerning(ushort left, ushort right)
    {
        if (_kernPairsOffset < 0)
        {
            return 0;
        }

        FontData kern = Table("kern");
        uint wanted = ((uint)left << 16) | right;
        int low = 0;
        int high = _kernPairs - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int at = _kernPairsOffset + (middle * 6);
            uint pair = kern.U32(at);
            if (pair == wanted)
            {
                return kern.I16(at + 4);
            }

            if (pair < wanted)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return 0;
    }

    /// <summary>The value from GPOS alone. Values from different lookups add up; within a lookup the first subtable that applies wins.</summary>
    internal int GposKerning(ushort left, ushort right)
    {
        if (_pairLookups.Count == 0)
        {
            return 0;
        }

        FontData gpos = Table("GPOS");
        int total = 0;
        foreach (int[] subtables in _pairLookups)
        {
            foreach (int subtable in subtables)
            {
                if (PairAdjustment(gpos, subtable, left, right, out int value))
                {
                    total += value;
                    break;
                }
            }
        }

        return total;
    }

    private void ReadKerning()
    {
        if (HasTable("GPOS"))
        {
            ReadGposKerning(Table("GPOS"));
        }

        // Read even when GPOS has kerning: Kerning() only falls back to it, but tests compare the two.
        if (HasTable("kern"))
        {
            ReadKernTable(Table("kern"));
        }
    }

    private void ReadGposKerning(FontData gpos)
    {
        if (gpos.U16(0) != 1)
        {
            return; // Unknown major version: no kerning rather than a guess.
        }

        int features = gpos.U16(6);
        int lookups = gpos.U16(8);
        int lookupCount = gpos.U16(lookups);

        // The lookups the kern features use, each once, in lookup-list order (the order they're applied in).
        var used = new SortedSet<int>();
        int featureCount = gpos.U16(features);
        for (int f = 0; f < featureCount; f++)
        {
            int record = features + 2 + (f * 6);
            if (gpos.U32(record) != Tag("kern"))
            {
                continue;
            }

            int feature = features + gpos.U16(record + 4);
            int indexCount = gpos.U16(feature + 2);
            for (int i = 0; i < indexCount; i++)
            {
                int index = gpos.U16(feature + 4 + (i * 2));
                if (index < lookupCount)
                {
                    used.Add(index);
                }
            }
        }

        int total = 0;
        foreach (int index in used)
        {
            int lookup = lookups + gpos.U16(lookups + 2 + (index * 2));
            int type = gpos.U16(lookup);
            int count = gpos.U16(lookup + 4);
            var pairs = new List<int>();
            for (int s = 0; s < count; s++)
            {
                int subtable = lookup + gpos.U16(lookup + 6 + (s * 2));
                int subtableType = type;
                if (type == GposExtension)
                {
                    subtableType = gpos.U16(subtable + 2);
                    subtable += checked((int)gpos.U32(subtable + 4));
                }

                if (subtableType == GposPairAdjustment)
                {
                    if (++total > MaxKerningSubtables)
                    {
                        throw gpos.Damaged($"the kern feature has more than {MaxKerningSubtables} pair adjustment subtables");
                    }

                    pairs.Add(subtable);
                }
            }

            if (pairs.Count > 0)
            {
                _pairLookups.Add([.. pairs]);
            }
        }
    }

    private void ReadKernTable(FontData kern)
    {
        if (kern.U16(0) != 0)
        {
            return; // Apple's version 1 table: not supported, no kerning.
        }

        int tables = kern.U16(2);
        int at = 4;
        for (int t = 0; t < tables; t++)
        {
            int length = kern.U16(at + 2);
            int coverage = kern.U16(at + 4);
            bool horizontal = (coverage & 0x1) != 0;
            bool plain = (coverage & 0x6) == 0; // Not minimum values, not cross-stream.
            if (coverage >> 8 == 0 && horizontal && plain)
            {
                _kernPairs = kern.U16(at + 6);
                _kernPairsOffset = at + 14;
                kern.Slice(_kernPairsOffset, _kernPairs * 6); // Checks the pairs are inside the table.
                return;
            }

            if (length < 6)
            {
                return;
            }

            at += length;
        }
    }

    /// <summary>Applies one pair adjustment subtable (format 1, glyph pairs, or format 2, class pairs).</summary>
    private static bool PairAdjustment(FontData gpos, int subtable, ushort left, ushort right, out int value)
    {
        value = 0;
        int format = gpos.U16(subtable);
        int coverageIndex = CoverageIndex(gpos, subtable + gpos.U16(subtable + 2), left);
        if (coverageIndex < 0)
        {
            return false;
        }

        int format1 = gpos.U16(subtable + 4);
        int format2 = gpos.U16(subtable + 6);
        int size1 = ValueRecordSize(format1);
        int size2 = ValueRecordSize(format2);
        if (format == 1)
        {
            int pairSets = gpos.U16(subtable + 8);
            if (coverageIndex >= pairSets)
            {
                return false;
            }

            int pairSet = subtable + gpos.U16(subtable + 10 + (coverageIndex * 2));
            int count = gpos.U16(pairSet);
            int recordSize = 2 + size1 + size2;
            int low = 0;
            int high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) >>> 1;
                int record = pairSet + 2 + (middle * recordSize);
                ushort second = gpos.U16(record);
                if (second == right)
                {
                    value = XAdvance(gpos, record + 2, format1);
                    return true;
                }

                if (second < right)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return false; // Not this pair: a later subtable may have it.
        }

        if (format == 2)
        {
            int class1 = ClassOf(gpos, subtable + gpos.U16(subtable + 8), left);
            int class2 = ClassOf(gpos, subtable + gpos.U16(subtable + 10), right);
            int class1Count = gpos.U16(subtable + 12);
            int class2Count = gpos.U16(subtable + 14);
            if (class1 >= class1Count || class2 >= class2Count)
            {
                return true; // Covered, so this subtable applies, with no adjustment.
            }

            int record = subtable + 16 + (((class1 * class2Count) + class2) * (size1 + size2));
            value = XAdvance(gpos, record, format1);
            return true;
        }

        return false;
    }

    private static int ValueRecordSize(int format) => System.Numerics.BitOperations.PopCount((uint)format & 0xFF) * 2;

    /// <summary>XAdvance (bit 0x4) from a value record, which comes after XPlacement and YPlacement when present.</summary>
    private static int XAdvance(FontData gpos, int record, int format)
    {
        if ((format & 0x4) == 0)
        {
            return 0;
        }

        int before = System.Numerics.BitOperations.PopCount((uint)format & 0x3) * 2;
        return gpos.I16(record + before);
    }

    private static int CoverageIndex(FontData gpos, int coverage, ushort glyph)
    {
        int format = gpos.U16(coverage);
        int count = gpos.U16(coverage + 2);
        int low = 0;
        int high = count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (format == 1)
            {
                ushort found = gpos.U16(coverage + 4 + (middle * 2));
                if (found == glyph)
                {
                    return middle;
                }

                if (found < glyph)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            else if (format == 2)
            {
                int range = coverage + 4 + (middle * 6);
                ushort start = gpos.U16(range);
                ushort end = gpos.U16(range + 2);
                if (glyph < start)
                {
                    high = middle - 1;
                }
                else if (glyph > end)
                {
                    low = middle + 1;
                }
                else
                {
                    return gpos.U16(range + 4) + glyph - start;
                }
            }
            else
            {
                throw gpos.Damaged($"coverage format {format} is unknown");
            }
        }

        return -1;
    }

    private static int ClassOf(FontData gpos, int classDef, ushort glyph)
    {
        int format = gpos.U16(classDef);
        if (format == 1)
        {
            int start = gpos.U16(classDef + 2);
            int count = gpos.U16(classDef + 4);
            return glyph >= start && glyph < start + count ? gpos.U16(classDef + 6 + ((glyph - start) * 2)) : 0;
        }

        if (format == 2)
        {
            int count = gpos.U16(classDef + 2);
            int low = 0;
            int high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) >>> 1;
                int range = classDef + 4 + (middle * 6);
                ushort start = gpos.U16(range);
                ushort end = gpos.U16(range + 2);
                if (glyph < start)
                {
                    high = middle - 1;
                }
                else if (glyph > end)
                {
                    low = middle + 1;
                }
                else
                {
                    return gpos.U16(range + 4);
                }
            }

            return 0;
        }

        throw gpos.Damaged($"class definition format {format} is unknown");
    }
}
