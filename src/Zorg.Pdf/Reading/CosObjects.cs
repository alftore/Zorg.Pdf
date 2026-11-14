using System;
using System.Collections.Generic;

namespace Zorg.Pdf.Reading;

/// <summary>A value in an existing PDF file: null, boolean, number, name, string, array, dictionary, reference or stream.</summary>
internal abstract class CosValue
{
    /// <summary>The value as a whole number, or null when it isn't one.</summary>
    public long? AsInteger => this switch
    {
        CosInteger i => i.Value,
        CosReal r when r.Value == Math.Floor(r.Value) && Math.Abs(r.Value) < 1e15 => (long)r.Value,
        _ => null,
    };

    /// <summary>The value as a number, or null when it isn't one.</summary>
    public double? AsNumber => this switch
    {
        CosInteger i => i.Value,
        CosReal r => r.Value,
        _ => null,
    };
}

internal sealed class CosNull : CosValue
{
    public static readonly CosNull Value = new();

    private CosNull()
    {
    }
}

internal sealed class CosBoolean(bool value) : CosValue
{
    public bool Value { get; } = value;
}

internal sealed class CosInteger(long value) : CosValue
{
    public long Value { get; } = value;
}

internal sealed class CosReal(double value) : CosValue
{
    public double Value { get; } = value;
}

internal sealed class CosName(string value) : CosValue
{
    public string Value { get; } = value;
}

internal sealed class CosString(byte[] value, bool hex) : CosValue
{
    public byte[] Value { get; } = value;

    /// <summary>Written as &lt;hex&gt; rather than (literal).</summary>
    public bool Hex { get; } = hex;
}

internal sealed class CosArray(List<CosValue> items) : CosValue
{
    public List<CosValue> Items { get; } = items;
}

internal sealed class CosDictionary(Dictionary<string, CosValue> entries) : CosValue
{
    public Dictionary<string, CosValue> Entries { get; } = entries;

    public CosValue? this[string key] => Entries.TryGetValue(key, out CosValue? value) ? value : null;
}

internal sealed class CosReference(int number, int generation) : CosValue
{
    public int Number { get; } = number;

    public int Generation { get; } = generation;
}

/// <summary>An operator in a content stream, such as <c>Tj</c>.</summary>
internal sealed class CosOperator(string name) : CosValue
{
    public string Name { get; } = name;
}

/// <summary>A stream object: its dictionary and its data as stored, filters not undone.</summary>
internal sealed class CosStream(CosDictionary dictionary, ReadOnlyMemory<byte> raw) : CosValue
{
    public CosDictionary Dictionary { get; } = dictionary;

    public ReadOnlyMemory<byte> Raw { get; } = raw;
}
