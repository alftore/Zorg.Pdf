using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf.Rendering;

/// <summary>
/// Image blocks: sized from the block or the image's pixels (at 96 dpi), scaled down to fit the content area,
/// moved to a new page when they don't fit, and tagged as a <c>Figure</c> with alt text and a bounding box, or drawn
/// as an artifact when decorative.
/// </summary>
internal sealed partial class RenderEngine
{
    /// <summary>Space after an image, in points: about the space after a paragraph.</summary>
    internal const double ImageSpaceAfter = 6;

    /// <summary>Points per pixel when an image has no size of its own: 96 dpi, as on screen.</summary>
    private const double PointsPerPixel = 0.75;

    private async ValueTask PlaceImageAsync(int index, Image image, CancellationToken cancellationToken)
    {
        if (image.Source is null || !_images.TryGet(image.Source, out ImageSet.Entry entry))
        {
            return; // Reported before the render started.
        }

        (double width, double height) = ImageSize(image, entry, _geometry.ContentWidth, _geometry.ContentHeight);
        double needed = height;
        if (image.KeepWithNext && NextFirstHeight(index) is { } next && height + ImageSpaceAfter + next <= _geometry.ContentHeight)
        {
            needed += ImageSpaceAfter + next;
        }

        if (!_pageEmpty && _y - needed < _geometry.Bottom)
        {
            await NewPageAsync(cancellationToken).ConfigureAwait(false);
        }

        double x = image.Align switch
        {
            TextAlign.Center => _geometry.Left + ((_geometry.ContentWidth - width) / 2),
            TextAlign.Right => _geometry.Right - width,
            _ => _geometry.Left,
        };
        double bottom = _y - height;
        _pageImages.Add(entry);
        Tagger.Element? figure = null;
        if (image.Decorative)
        {
            _content.BeginArtifact("Layout"u8, default);
        }
        else
        {
            figure = _tagger.Begin(StructureRole.Figure);
            figure.Alt = image.Alt;
            figure.BBox = (x, bottom, x + width, _y);
            _content.BeginMarkedContent(Tagger.Name(StructureRole.Figure), _tagger.Mark(figure, _page));
        }

        DrawImage(entry, x, bottom, width, height);
        _content.EndMarkedContent();
        if (figure is not null)
        {
            _tagger.End(figure, _documentWriter.Pages);
        }

        _y = bottom - ImageSpaceAfter;
        _pageEmpty = false;
    }

    /// <summary>Draws an image in the unit square scaled to its box; the XObject is written the first time.</summary>
    private void DrawImage(ImageSet.Entry entry, double x, double y, double width, double height)
    {
        (byte[] name, _) = _images.Use(entry, _writer);
        _content.SaveState().Place(x, y, width, height).DrawXObject(name).RestoreState();
    }

    /// <summary>
    /// The size an image is drawn at: its width and height if both are set, one of them with the aspect ratio kept,
    /// or its pixels at 96 dpi; then scaled down, keeping the aspect ratio, to fit <paramref name="maxWidth"/> and
    /// <paramref name="maxHeight"/>.
    /// </summary>
    private static (double Width, double Height) ImageSize(Image image, ImageSet.Entry entry, double maxWidth, double maxHeight)
    {
        double aspect = (double)entry.Height / entry.Width;
        (double width, double height) = (image.Width, image.Height) switch
        {
            ({ } w, { } h) => (w.Points, h.Points),
            ({ } w, null) => (w.Points, w.Points * aspect),
            (null, { } h) => (h.Points / aspect, h.Points),
            _ => (entry.Width * PointsPerPixel, entry.Height * PointsPerPixel),
        };
        double scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return (width * scale, height * scale);
    }
}
