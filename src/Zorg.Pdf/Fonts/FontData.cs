using System;
using System.Buffers.Binary;

namespace Zorg.Pdf.Fonts;

/// <summary>
/// Big-endian reads with bounds checks. Font files can come from users, so every read is checked, and a damaged file
/// gives a <see cref="PdfException"/> that names the table, never an <see cref="IndexOutOfRangeException"/>.
/// </summary>
internal readonly ref struct FontData
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _table;

    public FontData(ReadOnlySpan<byte> data, string table)
    {
        _data = data;
        _table = table;
    }

    public int Length => _data.Length;

    public ReadOnlySpan<byte> Span => _data;

    public ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(Slice(at, 2));

    public short I16(int at) => BinaryPrimitives.ReadInt16BigEndian(Slice(at, 2));

    public uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(Slice(at, 4));

    public int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(Slice(at, 4));

    public ReadOnlySpan<byte> Slice(int at, int length)
    {
        if (at < 0 || length < 0 || (long)at + length > _data.Length)
        {
            throw Damaged($"a read of {length} bytes at {at} is outside the table ({_data.Length} bytes)");
        }

        return _data.Slice(at, length);
    }

    public PdfException Damaged(string detail) => Damaged(_table, detail);

    public static PdfException Damaged(string table, string detail) =>
        new($"The font file is damaged or not supported: in the '{table}' table, {detail}.");
}
