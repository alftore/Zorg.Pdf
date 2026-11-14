using System;
using System.Collections.Generic;

namespace Zorg.Pdf.Reading;

/// <summary>Reads a page's content streams: joined, with marked content taken out, and what colour spaces they use.</summary>
internal static class CosContent
{
    /// <summary>
    /// The page's content, its streams decoded and joined. Marked-content operators (BDC, BMC, EMC, MP, DP) are taken
    /// out: they tag the old document's structure, which isn't copied, and the copy is drawn inside an artifact.
    /// </summary>
    public static byte[] Read(CosFile file, CosPage page, out bool usesCmyk)
    {
        var joined = new List<byte>();
        CosValue contents = file.Resolve(page.Dictionary["Contents"]);
        IEnumerable<CosValue> parts = contents is CosArray array ? array.Items : [page.Dictionary["Contents"] ?? CosNull.Value];
        foreach (CosValue part in parts)
        {
            if (Stream(file, part) is { } stream)
            {
                joined.AddRange(file.Decode(stream));
                joined.Add((byte)'\n');
            }
        }

        return Strip([.. joined], out usesCmyk);
    }

    private static CosStream? Stream(CosFile file, CosValue value) => value is CosReference r ? file.Object(r.Number) as CosStream : value as CosStream;

    /// <summary>The content without marked-content operators; inline images are copied untouched.</summary>
    public static byte[] Strip(byte[] content, out bool usesCmyk)
    {
        usesCmyk = false;
        var output = new List<byte>(content.Length);
        var lexer = new CosLexer(content);
        int operandsStart = 0;
        while (true)
        {
            lexer.SkipSpace();
            if (lexer.AtEnd)
            {
                break;
            }

            int tokenStart = lexer.Position;
            CosValue value = lexer.Read(operators: true) ?? CosNull.Value;
            if (value is CosName { Value: "DeviceCMYK" or "CMYK" })
            {
                usesCmyk = true;
            }

            if (value is not CosOperator op)
            {
                if (tokenStart < operandsStart)
                {
                    operandsStart = tokenStart;
                }

                continue;
            }

            int end = lexer.Position;
            switch (op.Name)
            {
                case "BDC" or "BMC" or "EMC" or "MP" or "DP":
                    break; // Dropped, with its operands.
                case "ID":
                    // Inline image data: binary, up to whitespace, "EI", whitespace.
                    int data = end + 1;
                    int ei = FindEndOfInlineImage(content, data);
                    end = ei;
                    lexer.Position = ei;
                    Copy(content, operandsStart, end, output);
                    break;
                default:
                    usesCmyk |= op.Name is "k" or "K";
                    Copy(content, operandsStart, end, output);
                    break;
            }

            operandsStart = lexer.Position;
        }

        return [.. output];
    }

    private static void Copy(byte[] content, int start, int end, List<byte> output)
    {
        output.AddRange(content.AsSpan(start, end - start));
        output.Add((byte)'\n');
    }

    private static int FindEndOfInlineImage(byte[] content, int from)
    {
        for (int i = Math.Max(from, 1); i + 1 < content.Length; i++)
        {
            if (content[i] == 'E' && content[i + 1] == 'I' && CosLexer.IsSpace(content[i - 1]) && (i + 2 == content.Length || CosLexer.IsSpace(content[i + 2]) || CosLexer.IsDelimiter(content[i + 2])))
            {
                return i + 2;
            }
        }

        throw PdfReadException.Malformed("An inline image has no end.");
    }
}
