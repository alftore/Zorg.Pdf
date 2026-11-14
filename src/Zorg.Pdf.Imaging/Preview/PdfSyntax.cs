using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zorg.Pdf.Imaging.Preview;

/// <summary>A PDF value: null, boolean, number, name, string, array, dictionary or reference.</summary>
internal abstract record PdfValue
{
    public double Number => this is PdfNumber n ? n.Value : throw PdfPreviewException.Malformed("Expected a number.");

    public int Integer => this is PdfNumber n && n.Value == Math.Floor(n.Value) && Math.Abs(n.Value) <= int.MaxValue ? (int)n.Value : throw PdfPreviewException.Malformed("Expected a whole number.");
}

internal sealed record PdfNull : PdfValue
{
    public static readonly PdfNull Value = new();
}

internal sealed record PdfBoolean(bool Value) : PdfValue;

internal sealed record PdfNumber(double Value) : PdfValue;

internal sealed record PdfName(string Value) : PdfValue;

internal sealed record PdfString(byte[] Value) : PdfValue;

internal sealed record PdfArray(List<PdfValue> Items) : PdfValue;

internal sealed record PdfDictionary(Dictionary<string, PdfValue> Entries) : PdfValue
{
    public PdfValue? this[string key] => Entries.TryGetValue(key, out PdfValue? value) ? value : null;
}

internal sealed record PdfRef(int ObjectNumber) : PdfValue;

/// <summary>An operator in a content stream, such as <c>Tj</c>.</summary>
internal sealed record PdfOperator(string Name) : PdfValue;

/// <summary>
/// Reads PDF tokens and objects from bytes. Strict and small: what Zorg.Pdf writes, plus the common forms around it.
/// Nesting and string lengths are bounded, so a hostile file can't exhaust the stack or memory here.
/// </summary>
internal ref struct PdfLexer(ReadOnlySpan<byte> data, int position = 0)
{
    private const int MaxDepth = 64;

    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; } = position;

    public readonly bool AtEnd => Position >= _data.Length;

    /// <summary>The next value; an operator when content streams allow bare keywords.</summary>
    public PdfValue? Read(bool operators = false, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw PdfPreviewException.Malformed("Objects are nested too deeply.");
        }

        SkipSpace();
        if (AtEnd)
        {
            return null;
        }

        byte c = _data[Position];
        switch (c)
        {
            case (byte)'/':
                return new PdfName(ReadName());
            case (byte)'(':
                return new PdfString(ReadLiteral());
            case (byte)'[':
                Position++;
                var items = new List<PdfValue>();
                while (true)
                {
                    SkipSpace();
                    if (AtEnd)
                    {
                        throw PdfPreviewException.Malformed("An array isn't closed.");
                    }

                    if (_data[Position] == ']')
                    {
                        Position++;
                        return new PdfArray(items);
                    }

                    items.Add(Read(operators: false, depth + 1) ?? throw PdfPreviewException.Malformed("An array isn't closed."));
                }

            case (byte)'<' when Position + 1 < _data.Length && _data[Position + 1] == '<':
                Position += 2;
                var entries = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
                while (true)
                {
                    SkipSpace();
                    if (Position + 1 < _data.Length && _data[Position] == '>' && _data[Position + 1] == '>')
                    {
                        Position += 2;
                        return new PdfDictionary(entries);
                    }

                    if (AtEnd || _data[Position] != '/')
                    {
                        throw PdfPreviewException.Malformed("A dictionary key isn't a name.");
                    }

                    string key = ReadName();
                    entries[key] = Read(operators: false, depth + 1) ?? throw PdfPreviewException.Malformed("A dictionary isn't closed.");
                }

            case (byte)'<':
                return new PdfString(ReadHex());
            case (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'):
                PdfNumber number = ReadNumber();
                if (!operators && number.Value >= 0 && number.Value == Math.Floor(number.Value) && TryReference(out int generation))
                {
                    _ = generation;
                    return new PdfRef((int)number.Value);
                }

                return number;
            default:
                string word = ReadWord();
                return word switch
                {
                    "true" => new PdfBoolean(true),
                    "false" => new PdfBoolean(false),
                    "null" => PdfNull.Value,
                    _ when operators && word.Length > 0 => new PdfOperator(word),
                    _ => throw PdfPreviewException.Malformed($"Unexpected \"{(word.Length == 0 ? ((char)c).ToString() : word)}\"."),
                };
        }
    }

    /// <summary>Skips white space and comments.</summary>
    public void SkipSpace()
    {
        while (!AtEnd)
        {
            byte c = _data[Position];
            if (IsSpace(c))
            {
                Position++;
            }
            else if (c == '%')
            {
                while (!AtEnd && _data[Position] is not ((byte)'\n' or (byte)'\r'))
                {
                    Position++;
                }
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Reads a keyword such as <c>obj</c> or <c>stream</c> if it's next.</summary>
    public bool TryKeyword(string keyword)
    {
        SkipSpace();
        int start = Position;
        string word = ReadWord();
        if (word == keyword)
        {
            return true;
        }

        Position = start;
        return false;
    }

    public static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>After a whole number: "G R" makes it a reference.</summary>
    private bool TryReference(out int generation)
    {
        int start = Position;
        generation = 0;
        SkipSpace();
        int digits = Position;
        while (!AtEnd && char.IsAsciiDigit((char)_data[Position]))
        {
            Position++;
        }

        if (Position > digits && Position < _data.Length && IsSpace(_data[Position]))
        {
            generation = int.Parse(_data[digits..Position], NumberStyles.None, CultureInfo.InvariantCulture);
            SkipSpace();
            if (!AtEnd && _data[Position] == 'R' && (Position + 1 == _data.Length || IsSpace(_data[Position + 1]) || IsDelimiter(_data[Position + 1])))
            {
                Position++;
                return true;
            }
        }

        Position = start;
        return false;
    }

    private PdfNumber ReadNumber()
    {
        int start = Position;
        while (!AtEnd && _data[Position] is (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'))
        {
            Position++;
        }

        ReadOnlySpan<byte> text = _data[start..Position];
        if (text.Length > 32 || !double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            throw PdfPreviewException.Malformed("A number can't be read.");
        }

        return new PdfNumber(value);
    }

    private string ReadWord()
    {
        int start = Position;
        while (!AtEnd && !IsSpace(_data[Position]) && !IsDelimiter(_data[Position]))
        {
            Position++;
        }

        return System.Text.Encoding.Latin1.GetString(_data[start..Position]);
    }

    private string ReadName()
    {
        Position++; // '/'
        var name = new System.Text.StringBuilder();
        while (!AtEnd && !IsSpace(_data[Position]) && !IsDelimiter(_data[Position]))
        {
            byte c = _data[Position++];
            if (c == '#' && Position + 1 < _data.Length && byte.TryParse(_data.Slice(Position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte escaped))
            {
                name.Append((char)escaped);
                Position += 2;
            }
            else
            {
                name.Append((char)c);
            }

            if (name.Length > 127)
            {
                throw PdfPreviewException.Malformed("A name is too long.");
            }
        }

        return name.ToString();
    }

    private byte[] ReadHex()
    {
        Position++; // '<'
        var bytes = new List<byte>();
        int high = -1;
        while (true)
        {
            if (AtEnd)
            {
                throw PdfPreviewException.Malformed("A hex string isn't closed.");
            }

            byte c = _data[Position++];
            if (c == '>')
            {
                break;
            }

            if (IsSpace(c))
            {
                continue;
            }

            int digit = char.IsAsciiHexDigit((char)c) ? Convert.ToInt32(((char)c).ToString(), 16) : throw PdfPreviewException.Malformed("A hex string has a character that isn't hex.");
            if (high < 0)
            {
                high = digit;
            }
            else
            {
                bytes.Add((byte)((high << 4) | digit));
                high = -1;
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return [.. bytes];
    }

    private byte[] ReadLiteral()
    {
        Position++; // '('
        var bytes = new List<byte>();
        int nesting = 1;
        while (true)
        {
            if (AtEnd)
            {
                throw PdfPreviewException.Malformed("A string isn't closed.");
            }

            byte c = _data[Position++];
            switch (c)
            {
                case (byte)'(':
                    nesting++;
                    bytes.Add(c);
                    break;
                case (byte)')':
                    if (--nesting == 0)
                    {
                        return [.. bytes];
                    }

                    bytes.Add(c);
                    break;
                case (byte)'\\' when !AtEnd:
                    byte e = _data[Position++];
                    switch (e)
                    {
                        case (byte)'n':
                            bytes.Add(10);
                            break;
                        case (byte)'r':
                            bytes.Add(13);
                            break;
                        case (byte)'t':
                            bytes.Add(9);
                            break;
                        case (byte)'b':
                            bytes.Add(8);
                            break;
                        case (byte)'f':
                            bytes.Add(12);
                            break;
                        case (byte)'\r':
                            if (!AtEnd && _data[Position] == '\n')
                            {
                                Position++;
                            }

                            break;
                        case (byte)'\n':
                            break;
                        case >= (byte)'0' and <= (byte)'7':
                            int value = e - '0';
                            for (int i = 0; i < 2 && !AtEnd && _data[Position] is >= (byte)'0' and <= (byte)'7'; i++)
                            {
                                value = (value * 8) + (_data[Position++] - '0');
                            }

                            bytes.Add((byte)value);
                            break;
                        default:
                            bytes.Add(e);
                            break;
                    }

                    break;
                default:
                    bytes.Add(c);
                    break;
            }

            if (bytes.Count > 1 << 20)
            {
                throw PdfPreviewException.Malformed("A string is too long.");
            }
        }
    }
}
