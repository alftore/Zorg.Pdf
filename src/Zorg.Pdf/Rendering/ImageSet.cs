using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Images;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// The images of one render. Every image is loaded and checked before the first byte is written (a missing slot, a
/// damaged file or one over the limits is an error then), and kept as encoded XObject data only. Each distinct
/// source becomes one XObject, written the first time a page draws it and then shared; its data is released once
/// written.
/// </summary>
internal sealed class ImageSet
{
    private readonly Dictionary<ImageSource, Entry> _entries = [];
    private int _nextName = 1;

    private ImageSet()
    {
    }

    /// <summary>Loads and checks every image in the content, header and footer.</summary>
    public static async ValueTask<ImageSet> LoadAsync(PdfDocument document, RenderOptions options, DiagnosticSink sink, bool synchronous, CancellationToken cancellationToken)
    {
        var set = new ImageSet();
        foreach ((Image image, string path) in All(document))
        {
            if (image.Source is null || set._entries.ContainsKey(image.Source))
            {
                continue;
            }

            ReadOnlyMemory<byte>? bytes = await BytesAsync(image.Source, options, sink, synchronous, path, cancellationToken).ConfigureAwait(false);
            if (bytes is not { } data)
            {
                continue;
            }

            try
            {
                if (data.Length > options.Limits.MaxImageBytes)
                {
                    throw new PdfLimitException(string.Create(CultureInfo.InvariantCulture, $"The image is {data.Length:N0} bytes, more than the {options.Limits.MaxImageBytes:N0} allowed (RenderOptions.Limits.MaxImageBytes)."));
                }

                PreparedImage prepared = JpegReader.IsJpeg(data.Span) ? JpegReader.Read(data, options.Limits)
                    : PngReader.IsPng(data.Span) ? PngReader.Read(data, options.Limits)
                    : throw new PdfException("The image isn't a JPEG or a PNG.");
                set._entries[image.Source] = new Entry(prepared);
            }
            catch (PdfException ex) when (ex is not PdfLimitException)
            {
                sink.Error(DiagnosticCodes.ImageUnreadable, ex.Message, $"{path}/source");
            }
            catch (PdfLimitException ex)
            {
                sink.Error(DiagnosticCodes.ImageTooLarge, ex.Message, $"{path}/source");
            }
        }

        return set;
    }

    /// <summary>Every image block with its JSON pointer: the content's, then the header's and the footer's.</summary>
    public static IEnumerable<(Image Image, string Path)> All(PdfDocument document)
    {
        for (int i = 0; i < document.Content.Length; i++)
        {
            if (document.Content[i] is Image image)
            {
                yield return (image, $"/content/{i}");
            }
        }

        foreach ((PageBand? band, string path) in new[] { (document.Header, "/header"), (document.Footer, "/footer") })
        {
            for (int i = 0; i < (band?.Content.Length ?? 0); i++)
            {
                if (band!.Content[i] is Image image)
                {
                    yield return (image, $"{path}/content/{i}");
                }
            }
        }
    }

    public bool TryGet(ImageSource source, out Entry entry) => _entries.TryGetValue(source, out entry!);

    /// <summary>The image's resource name and object, writing it (and its soft mask) the first time.</summary>
    public (byte[] Name, PdfReference Reference) Use(Entry entry, PdfObjectWriter writer)
    {
        if (entry.Name is null)
        {
            entry.Name = System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Im{_nextName++}"));
            entry.Reference = writer.Allocate();
            PreparedImage image = entry.Image!;
            PdfReference? mask = image.Mask is null ? null : writer.Allocate();
            Write(writer, entry.Reference, image, mask);
            if (image.Mask is { } alpha)
            {
                Write(writer, mask!.Value, alpha, null);
            }

            entry.Image = null; // Written: only the size is needed from here on.
        }

        return (entry.Name, entry.Reference);
    }

    private static async ValueTask<ReadOnlyMemory<byte>?> BytesAsync(ImageSource source, RenderOptions options, DiagnosticSink sink, bool synchronous, string path, CancellationToken cancellationToken)
    {
        switch (source)
        {
            case BytesImageSource bytes:
                return bytes.Bytes;
            case SlotImageSource slot when !options.Data.Images.TryGetValue(slot.Slot, out PdfData.ImageSlot? data):
                sink.Error(DiagnosticCodes.MissingSlot, $"The image names the data slot \"{slot.Slot}\", but the render has no image for it. Supply it: RenderOptions.Data = PdfData.Empty.WithImage(\"{slot.Slot}\", bytes).", $"{path}/source");
                return null;
            case SlotImageSource slot:
                PdfData.ImageSlot found = options.Data.Images[slot.Slot];
                if (found.Bytes is { } ready)
                {
                    return ready;
                }

                if (synchronous)
                {
                    sink.Error(DiagnosticCodes.AsyncRowsInSyncRender, $"The image for \"{slot.Slot}\" is loaded asynchronously, which a synchronous Render can't wait for. Use RenderAsync, or supply the bytes.", $"{path}/source");
                    return null;
                }

                return await found.Load!(cancellationToken).ConfigureAwait(false);
            default:
                return null;
        }
    }

    private static void Write(PdfObjectWriter writer, PdfReference reference, PreparedImage image, PdfReference? mask)
    {
        writer.BeginStreamObject(reference);
        writer.Name("Type"u8);
        writer.Name("XObject"u8);
        writer.Name("Subtype"u8);
        writer.Name("Image"u8);
        writer.Name("Width"u8);
        writer.Integer(image.Width);
        writer.Name("Height"u8);
        writer.Integer(image.Height);
        writer.Name("ColorSpace"u8);
        switch (image.ColorSpace)
        {
            case ImageColorSpace.Gray:
                writer.Name("DeviceGray"u8);
                break;
            case ImageColorSpace.Rgb:
                writer.Name("DeviceRGB"u8);
                break;
            case ImageColorSpace.Cmyk:
                writer.Name("DeviceCMYK"u8);
                break;
            default:
                writer.BeginArray();
                writer.Name("Indexed"u8);
                writer.Name("DeviceRGB"u8);
                writer.Integer((image.Palette.Length / 3) - 1);
                writer.HexString(image.Palette.Span);
                writer.EndArray();
                break;
        }

        writer.Name("BitsPerComponent"u8);
        writer.Integer(image.BitsPerComponent);
        writer.Name("Filter"u8);
        writer.Name(image.Dct ? "DCTDecode"u8 : "FlateDecode"u8);
        if (image.PngPredictorColors is { } colors)
        {
            writer.Name("DecodeParms"u8);
            writer.BeginDictionary();
            writer.Name("Predictor"u8);
            writer.Integer(15);
            writer.Name("Colors"u8);
            writer.Integer(colors);
            writer.Name("BitsPerComponent"u8);
            writer.Integer(image.BitsPerComponent);
            writer.Name("Columns"u8);
            writer.Integer(image.Width);
            writer.EndDictionary();
        }

        if (image.InvertedCmyk)
        {
            writer.Name("Decode"u8);
            writer.BeginArray();
            for (int i = 0; i < 4; i++)
            {
                writer.Integer(1);
                writer.Integer(0);
            }

            writer.EndArray();
        }

        if (mask is { } smask)
        {
            writer.Name("SMask"u8);
            writer.Reference(smask);
        }

        writer.EndStreamObject(image.Data.Span, compress: false);
    }

    /// <summary>One distinct image: its size in pixels, and its data until it's written.</summary>
    internal sealed class Entry(PreparedImage image)
    {
        public PreparedImage? Image { get; set; } = image;

        public int Width { get; } = image.Width;

        public int Height { get; } = image.Height;

        public byte[]? Name { get; set; }

        public PdfReference Reference { get; set; }
    }
}
