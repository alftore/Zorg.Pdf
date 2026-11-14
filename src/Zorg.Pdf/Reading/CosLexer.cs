using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zorg.Pdf.Reading;

/// <summary>
/// Reads PDF tokens and objects from bytes, tolerant of what other writers do. Nesting, names and strings are
/// bounded, so a hostile file can't exhaust the stack or memory here; every failure is a <see cref="PdfReadException"/>.
/// </summary>
internal ref struct CosLexer(ReadOnlySpan<byte> data, int position = 0)
{
    public const int MaxDepth = 100;
    private const int MaxName = 1024;
    private const int MaxString = 16 << 20;

    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; } = position;

    public readonly bool AtEnd => Position >= _data.Length;

    /// <summary>The next value, or null at the end; with <paramref name="operators"/>, bare keywords are operators.</summary>
    public CosValue? Read(bool operators = false, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw PdfReadException.Malformed("Objects are nested too deeply.");
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
                return new CosName(ReadName());
            case (byte)'(':
                return new CosString(ReadLiteral(), hex: false);
            case (byte)'[':
                Position++;
                var items = new List<CosValue>();
                while (true)
                {
                    SkipSpace();
                    if (AtEnd)
                    {
                        throw PdfReadException.Malformed("An array isn't closed.");
                    }

                    if (_data[Position] == ']')
                    {
                        Position++;
                        return new CosArray(items);
                    }

                    items.Add(Read(operators, depth + 1) ?? throw PdfReadException.Malformed("An array isn't closed."));
                }

            case (byte)'<' when Position + 1 < _data.Length && _data[Position + 1] == '<':
                Position += 2;
                var entries = new Dictionary<string, CosValue>(StringComparer.Ordinal);
                while (true)
                {
                    SkipSpace();
                    if (Position + 1 < _data.Length && _data[Position] == '>' && _data[Position + 1] == '>')
                    {
                        Position += 2;
                        return new CosDictionary(entries);
                    }

                    if (AtEnd || _data[Position] != '/')
                    {
                        throw PdfReadException.Malformed("A dictionary key isn't a name.");
                    }

                    string key = ReadName();
                    SkipSpace();
                    if (Position + 1 < _data.Length && _data[Position] == '>' && _data[Position + 1] == '>')
                    {
                        entries[key] = CosNull.Value; // A key without a value: treated as null, as viewers do.
                        continue;
                    }

                    entries[key] = Read(operators: false, depth + 1) ?? throw PdfReadException.Malformed("A dictionary isn't closed.");
                }

            case (byte)'<':
                return new CosString(ReadHex(), hex: true);
            case (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'):
                CosValue number = ReadNumber();
                if (!operators && number is CosInteger { Value: >= 0 and <= int.MaxValue } n && TryReference(out int generation))
                {
                    return new CosReference((int)n.Value, generation);
                }

                return number;
            case (byte)')' or (byte)'>' or (byte)']' or (byte)'}' or (byte)'{' when !operators:
                throw PdfReadException.Malformed($"Unexpected \"{(char)c}\".");
            case (byte)'{' or (byte)'}':
                Position++;
                return new CosOperator(((char)c).ToString());
            default:
                string word = ReadWord();
                return word switch
                {
                    "true" => new CosBoolean(true),
                    "false" => new CosBoolean(false),
                    "null" => CosNull.Value,
                    _ when operators && word.Length > 0 => new CosOperator(word),
                    _ => throw PdfReadException.Malformed($"Unexpected \"{(word.Length == 0 ? ((char)c).ToString() : word.Length > 20 ? word[..20] : word)}\"."),
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
        if (ReadWord() == keyword)
        {
            return true;
        }

        Position = start;
        return false;
    }

    /// <summary>A whole number, or null; the position moves past it only when it is one.</summary>
    public long? TryInteger()
    {
        SkipSpace();
        int start = Position;
        if (!AtEnd && (char.IsAsciiDigit((char)_data[Position]) || _data[Position] is (byte)'-' or (byte)'+'))
        {
            CosValue value = ReadNumber();
            if (value is CosInteger i)
            {
                return i.Value;
            }
        }

        Position = start;
        return null;
    }

    public static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>After a whole number: "G R" makes it a reference.</summary>
    private bool TryReference(out int generation)
    {
        int start = Position;
        generation = 0;
        SkipSpace();
        int digits = Position;
        while (!AtEnd && char.IsAsciiDigit((char)_data[Position]) && Position - digits < 10)
        {
            Position++;
        }

        if (Position > digits && !AtEnd && IsSpace(_data[Position]))
        {
            _ = int.TryParse(_data[digits..Position], NumberStyles.None, CultureInfo.InvariantCulture, out generation);
            SkipSpace();
            if (!AtEnd && _data[Position] == 'R' && (Position + 1 == _data.Length || IsSpace(_data[Position + 1]) || IsDelimiter(_data[Position + 1])))
            {
                Position++;
                return true;
            }
        }

        Position = start;
        generation = 0;
        return false;
    }

    /// <summary>A number. Sloppy forms other writers produce, such as "--5" or "4.", are read as viewers read them.</summary>
    private CosValue ReadNumber()
    {
        int start = Position;
        while (!AtEnd && _data[Position] is (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'))
        {
            Position++;
        }

        ReadOnlySpan<byte> text = _data[start..Position];
        int signs = 0;
        bool negative = false;
        while (signs < text.Length && text[signs] is (byte)'+' or (byte)'-')
        {
            negative ^= text[signs] == '-';
            signs++;
        }

        ReadOnlySpan<byte> digits = text[signs..];
        if (digits.Length > 40)
        {
            throw PdfReadException.Malformed("A number is too long.");
        }

        if (digits.IndexOf((byte)'.') < 0)
        {
            if (digits.IsEmpty)
            {
                return new CosInteger(0);
            }

            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long whole))
            {
                return new CosInteger(negative ? -whole : whole);
            }
        }

        int dot = digits.IndexOf((byte)'.');
        if (dot >= 0 && digits[(dot + 1)..].IndexOf((byte)'.') >= 0)
        {
            digits = digits[..(dot + 1 + digits[(dot + 1)..].IndexOf((byte)'.'))]; // "1.2.3": viewers stop at the second point.
        }

        if (digits.IsEmpty || digits.SequenceEqual("."u8))
        {
            return new CosReal(0);
        }

        if (!double.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            throw PdfReadException.Malformed("A number can't be read.");
        }

        return new CosReal(negative ? -value : value);
    }

    private string ReadWord()
    {
        int start = Position;
        while (!AtEnd && !IsSpace(_data[Position]) && !IsDelimiter(_data[Position]) && Position - start < MaxName)
        {
            Position++;
        }

        return Encoding.Latin1.GetString(_data[start..Position]);
    }

    /// <summary>A name, with #xx escapes undone; its bytes as Latin-1 characters (names are bytes, usually UTF-8 or ASCII).</summary>
    private string ReadName()
    {
        Position++; // '/'
        var name = new StringBuilder();
        while (!AtEnd && !IsSpace(_data[Position]) && !IsDelimiter(_data[Position]))
        {
            byte c = _data[Position++];
            if (c == '#' && Position + 1 < _data.Length && byte.TryParse(_data.Slice(Position, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte escaped))
            {
                name.Append((char)escaped);
                Position += 2;
            }
            else
            {
                name.Append((char)c);
            }

            if (name.Length > MaxName)
            {
                throw PdfReadException.Malformed("A name is too long.");
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
                throw PdfReadException.Malformed("A hex string isn't closed.");
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

            int digit = HexDigit(c);
            if (digit < 0)
            {
                throw PdfReadException.Malformed("A hex string has a character that isn't hex.");
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                bytes.Add((byte)((high << 4) | digit));
                high = -1;
                if (bytes.Count > MaxString)
                {
                    throw PdfReadException.Malformed("A string is too long.");
                }
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return [.. bytes];
    }

    public static int HexDigit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    private byte[] ReadLiteral()
    {
        Position++; // '('
        var bytes = new List<byte>();
        int nesting = 1;
        while (true)
        {
            if (AtEnd)
            {
                throw PdfReadException.Malformed("A string isn't closed.");
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

            if (bytes.Count > MaxString)
            {
                throw PdfReadException.Malformed("A string is too long.");
            }
        }
    }
}
