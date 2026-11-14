using System;

namespace Zorg.Pdf.Images;

/// <summary>The colour space of an image XObject.</summary>
internal enum ImageColorSpace
{
    Gray,
    Rgb,
    Cmyk,

    /// <summary>Palette indices into <see cref="PreparedImage.Palette"/> (RGB triples).</summary>
    Indexed,
}

/// <summary>
/// An image checked and turned into what an image XObject holds: the stream data already encoded (JPEG bytes as they
/// came, or Flate data), so a render keeps only compressed bytes between loading the image and writing it.
/// </summary>
internal sealed class PreparedImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required ImageColorSpace ColorSpace { get; init; }

    public required int BitsPerComponent { get; init; }

    /// <summary><c>/DCTDecode</c> (JPEG) when set, else <c>/FlateDecode</c>.</summary>
    public bool Dct { get; init; }

    /// <summary>An Adobe CMYK JPEG, stored inverted: drawn with <c>/Decode [1 0 1 0 1 0 1 0]</c>.</summary>
    public bool InvertedCmyk { get; init; }

    /// <summary>The PNG predictor's colour count when the data is PNG-filtered Flate data passed through (predictor 15).</summary>
    public int? PngPredictorColors { get; init; }

    /// <summary>RGB triples for <see cref="ImageColorSpace.Indexed"/>.</summary>
    public ReadOnlyMemory<byte> Palette { get; init; }

    /// <summary>The encoded stream data.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The alpha channel as an 8-bit grey soft mask, or null when the image is opaque.</summary>
    public PreparedImage? Mask { get; init; }
}
