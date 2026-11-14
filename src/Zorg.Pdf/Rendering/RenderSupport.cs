using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
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

    public TextStyle Named(string name) => _styles.TryGetValue(name, out TextStyle? style) ? _styles["body"].Merge(style) : _styles["body"];
}

/// <summary>
/// The fonts of one render, loaded and parsed, by family. Each family's regular face becomes one embedded font with a
/// page resource name (<c>/F1</c>, <c>/F2</c> ...) and an object reference allocated when a page first uses it.
/// </summary>
internal sealed class FontSet
{
    private readonly Dictionary<string, EmbeddedFont> _byFamily = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EmbeddedFont, (byte[] Name, PdfReference Reference)> _resources = [];

    private FontSet(string? defaultFamily) => DefaultFamily = defaultFamily;

    public string? DefaultFamily { get; }

    /// <summary>Loads every family the document needs. Problems become diagnostics; a family that fails is left out.</summary>
    public static async ValueTask<FontSet> LoadAsync(FontCollection fonts, IReadOnlyDictionary<string, string> needed, string? defaultFamily, DiagnosticSink sink, bool synchronous, CancellationToken cancellationToken)
    {
        var set = new FontSet(defaultFamily);
        foreach ((string family, string path) in needed)
        {
            if (!fonts.Families.TryGetValue(family, out FontFamily? found))
            {
                sink.Error(DiagnosticCodes.UnknownFontFamily, $"There's no font family named \"{family}\" in the render's fonts. Add it to RenderOptions.Fonts, or use one of: {string.Join(", ", fonts.Families.Keys.Order(StringComparer.OrdinalIgnoreCase))}.", path);
                continue;
            }

            if (found.Regular is not { } source)
            {
                sink.Error(DiagnosticCodes.UnknownFontFamily, $"The font family \"{family}\" has no regular face. Set FontFamily.Regular.", path);
                continue;
            }

            try
            {
                ReadOnlyMemory<byte> bytes = await source.ReadAsync(synchronous, cancellationToken).ConfigureAwait(false);
                set._byFamily[family] = new EmbeddedFont(TrueTypeFont.Parse(bytes));
            }
            catch (PdfException ex)
            {
                sink.Error(DiagnosticCodes.FontUnreadable, $"The regular face of \"{family}\" ({source.Describe()}) can't be used: {ex.Message}", path);
            }
            catch (System.IO.IOException ex)
            {
                sink.Error(DiagnosticCodes.FontUnreadable, $"The regular face of \"{family}\" ({source.Describe()}) can't be read: {ex.Message}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                sink.Error(DiagnosticCodes.FontUnreadable, $"The regular face of \"{family}\" ({source.Describe()}) can't be read: {ex.Message}", path);
            }
        }

        return set;
    }

    public bool TryGet(string family, out EmbeddedFont font) => _byFamily.TryGetValue(family, out font!);

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
}
