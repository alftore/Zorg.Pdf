using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Layout;
using Zorg.Pdf.Validation;
using Zorg.Pdf.Writing;

namespace Zorg.Pdf.Rendering;

/// <summary>Collects diagnostics in the order they're found.</summary>
internal sealed class DiagnosticSink
{
    private readonly List<PdfDiagnostic> _diagnostics = [];

    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(PdfDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

    public void AddRange(IEnumerable<PdfDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    public void Error(string code, string message, string path) => Add(new PdfDiagnostic(code, DiagnosticSeverity.Error, message, path));

    public void Warning(string code, string message, string path, int? page = null) =>
        Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, path) { Page = page });

    public void Info(string code, string message, string path) => Add(new PdfDiagnostic(code, DiagnosticSeverity.Info, message, path));

    public ImmutableArray<PdfDiagnostic> ToImmutable() => [.. _diagnostics];
}

/// <summary>
/// The effective style of a block: <c>body</c>, then the block's default named style (<c>heading1</c> ...), then its
/// own <see cref="Block.Style"/>. The document's style sheet merges over the built-in styles by name.
/// </summary>
internal sealed class StyleResolver
{
    private readonly ImmutableDictionary<string, TextStyle> _styles;

    public StyleResolver(StyleSheet? sheet)
    {
        ImmutableDictionary<string, TextStyle> styles = StyleSheet.Default.Styles;
        foreach ((string name, TextStyle style) in sheet?.Styles ?? ImmutableDictionary<string, TextStyle>.Empty)
        {
            styles = styles.SetItem(name, styles.TryGetValue(name, out TextStyle? builtIn) ? builtIn.Merge(style) : style);
        }

        _styles = styles;
        DefaultFontFamily = sheet?.DefaultFontFamily;
    }

    public string? DefaultFontFamily { get; }

    public TextStyle For(Block block)
    {
        string defaultName = block is Heading heading ? $"heading{Math.Clamp(heading.Level, 1, 6)}" : "body";
        TextStyle style = _styles["body"];
        if (defaultName != "body")
        {
            style = style.Merge(_styles[defaultName]);
        }

        return block.Style is { } name && _styles.TryGetValue(name, out TextStyle? named) ? style.Merge(named) : style;
    }

    /// <summary>A paragraph in a header or footer: <c>body</c>, then <c>header</c> or <c>footer</c>, then its own style.</summary>
    public TextStyle ForBand(Block block, string band) =>
        block.Style is { } name && _styles.TryGetValue(name, out TextStyle? own) ? Named(band).Merge(own) : Named(band);

    public TextStyle Named(string name) => _styles.TryGetValue(name, out TextStyle? style) ? _styles["body"].Merge(style) : _styles["body"];

    /// <summary>A table's header cells: <c>body</c>, then <c>tableHeader</c>.</summary>
    public TextStyle TableHeader() => Named("tableHeader");

    /// <summary>A table's body cells: <c>body</c>, then <c>tableCell</c>, then the table's own <see cref="Block.Style"/>.</summary>
    public TextStyle TableCell(Table table) =>
        table.Style is { } name && _styles.TryGetValue(name, out TextStyle? own) ? Named("tableCell").Merge(own) : Named("tableCell");
}

/// <summary>A face of a family: what a run of text asks for.</summary>
internal readonly record struct FaceKey(string Family, FontWeight Weight, FontStyle Style)
{
    /// <summary>"bold italic", "bold", "italic" or "regular".</summary>
    public string FaceName => (Weight, Style) switch
    {
        (FontWeight.Bold, FontStyle.Italic) => "bold italic",
        (FontWeight.Bold, _) => "bold",
        (_, FontStyle.Italic) => "italic",
        _ => "regular",
    };

    public override string ToString() => $"{Family} {FaceName}";
}

/// <summary>
/// The fonts of one render: for each face the text needs, a chain of the face and its family's fallbacks. Fonts come
/// from <see cref="RenderOptions.Fonts"/>, then <see cref="RenderOptions.FontResolver"/>, and are parsed once per
/// process through <see cref="FontCache"/>. Each parsed font becomes one <see cref="EmbeddedFont"/> per render, with a
/// page resource name (<c>/F1</c>, <c>/F2</c> ...) and an object reference allocated when a page first uses it.
/// </summary>
internal sealed class FontSet
{
    private readonly Dictionary<FaceKey, FontChain> _chains = [];
    private readonly Dictionary<FaceKey, EmbeddedFont?> _faces = [];
    private readonly Dictionary<FaceKey, (bool Bold, bool Italic)> _synthetic = [];
    private readonly Dictionary<TrueTypeFont, EmbeddedFont> _embedded = [];
    private readonly Dictionary<EmbeddedFont, (byte[] Name, PdfReference Reference)> _resources = [];
    private readonly FontCollection _collection;
    private readonly IFontResolver? _resolver;
    private readonly DiagnosticSink _sink;
    private readonly bool _synchronous;

    private FontSet(FontCollection collection, IFontResolver? resolver, DiagnosticSink sink, bool synchronous) =>
        (_collection, _resolver, _sink, _synchronous) = (collection, resolver, sink, synchronous);

    /// <summary>Loads every face the document needs, with fallbacks. Problems become diagnostics; a face that fails is left out.</summary>
    public static async ValueTask<FontSet> LoadAsync(RenderOptions options, IReadOnlyDictionary<FaceKey, string> needed, DiagnosticSink sink, bool synchronous, CancellationToken cancellationToken)
    {
        var set = new FontSet(options.Fonts, options.FontResolver, sink, synchronous);
        foreach ((FaceKey face, string path) in needed)
        {
            EmbeddedFont? primary = await set.FaceAsync(face, path, cancellationToken).ConfigureAwait(false);
            if (primary is null)
            {
                continue;
            }

            var fallbacks = new List<EmbeddedFont>();
            if (options.Fonts.Families.TryGetValue(face.Family, out FontFamily? family))
            {
                foreach (string name in family.Fallbacks)
                {
                    if (await set.FaceAsync(face with { Family = name }, path, cancellationToken).ConfigureAwait(false) is { } fallback && fallback != primary)
                    {
                        fallbacks.Add(fallback);
                    }
                }
            }

            (bool bold, bool italic) = set._synthetic.GetValueOrDefault(face);
            set._chains[face] = new FontChain(primary, fallbacks, bold, italic);
        }

        return set;
    }

    public bool TryGet(FaceKey face, out FontChain chain) => _chains.TryGetValue(face, out chain!);

    /// <summary>The page resource name of a font, allocating its object the first time a page uses it.</summary>
    public byte[] Use(EmbeddedFont font, PdfObjectWriter writer)
    {
        if (!_resources.TryGetValue(font, out (byte[] Name, PdfReference Reference) resource))
        {
            resource = (Encoding.ASCII.GetBytes($"F{_resources.Count + 1}"), writer.Allocate());
            _resources[font] = resource;
        }

        return resource.Name;
    }

    public PdfReference Reference(EmbeddedFont font) => _resources[font].Reference;

    /// <summary>Fonts some page used, in the order they were first used.</summary>
    public IEnumerable<(EmbeddedFont Font, byte[] Name, PdfReference Reference)> Used =>
        _resources.OrderBy(r => r.Value.Reference.Number).Select(r => (r.Key, r.Value.Name, r.Value.Reference));

    /// <summary>One face, loaded once per render whichever runs ask for it.</summary>
    private async ValueTask<EmbeddedFont?> FaceAsync(FaceKey face, string path, CancellationToken cancellationToken)
    {
        if (_faces.TryGetValue(face, out EmbeddedFont? known))
        {
            return known;
        }

        EmbeddedFont? font = null;
        FontSource? source = await SourceAsync(face, path, cancellationToken).ConfigureAwait(false);
        if (source is not null)
        {
            try
            {
                TrueTypeFont parsed = await source.LoadAsync(_synchronous, cancellationToken).ConfigureAwait(false);
                if (!_embedded.TryGetValue(parsed, out font))
                {
                    font = new EmbeddedFont(parsed);
                    _embedded[parsed] = font;
                }
            }
            catch (PdfException ex)
            {
                _sink.Error(DiagnosticCodes.FontUnreadable, $"The font for {face} ({source.Describe()}) can't be used: {ex.Message}", path);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _sink.Error(DiagnosticCodes.FontUnreadable, $"The font for {face} ({source.Describe()}) can't be read: {ex.Message}", path);
            }
        }

        _faces[face] = font;
        return font;
    }

    private async ValueTask<FontSource?> SourceAsync(FaceKey face, string path, CancellationToken cancellationToken)
    {
        if (_collection.Families.TryGetValue(face.Family, out FontFamily? family))
        {
            (FontSource? source, bool exact) = family.Face(face.Weight, face.Style);
            if (source is null)
            {
                _sink.Error(DiagnosticCodes.UnknownFontFamily, $"The font family \"{face.Family}\" has no regular face. Set FontFamily.Regular.", path);
            }
            else if (!exact)
            {
                // Synthesise what the chosen face lacks: thicken for bold, slant for italic.
                bool synthBold = face.Weight == FontWeight.Bold && !ReferenceEquals(source, family.Bold) && !ReferenceEquals(source, family.BoldItalic);
                bool synthItalic = face.Style == FontStyle.Italic && !ReferenceEquals(source, family.Italic) && !ReferenceEquals(source, family.BoldItalic);
                _synthetic[face] = (synthBold, synthItalic);
                string how = (synthBold, synthItalic) switch
                {
                    (true, true) => "thickened and slanted",
                    (true, false) => "thickened",
                    _ => "slanted",
                };
                _sink.Info(DiagnosticCodes.MissingFace, $"The font family \"{face.Family}\" has no {face.FaceName} face, so it's drawn {how} from a face it has. Add the real face for better type.", path);
            }

            return source;
        }

        if (_resolver is not null)
        {
            ValueTask<FontSource?> lookup = _resolver.ResolveAsync(new FontRequest(face.Family, face.Weight, face.Style), cancellationToken);
            if (_synchronous && !lookup.IsCompleted)
            {
                _sink.Error(DiagnosticCodes.UnknownFontFamily, $"The font resolver looked up \"{face.Family}\" asynchronously, which a synchronous render can't wait for. Use RenderAsync, or add the family to RenderOptions.Fonts.", path);
                return null;
            }

            if (await lookup.ConfigureAwait(false) is { } resolved)
            {
                return resolved;
            }
        }

        string where = _resolver is null ? "in the render's fonts" : "in the render's fonts or from its font resolver";
        string choices = _collection.Families.IsEmpty ? "" : ", or use one of: " + string.Join(", ", _collection.Families.Keys.Order(StringComparer.OrdinalIgnoreCase));
        _sink.Error(DiagnosticCodes.UnknownFontFamily, $"There's no font family named \"{face.Family}\" {where}. Add it to RenderOptions.Fonts{choices}.", path);
        return null;
    }
}
