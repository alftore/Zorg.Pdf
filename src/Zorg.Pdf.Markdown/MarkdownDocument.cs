using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ImageBlock = Zorg.Pdf.Image;
using MdTable = Markdig.Extensions.Tables.Table;
using PdfLink = Zorg.Pdf.Link;
using PdfList = Zorg.Pdf.List;
using PdfTable = Zorg.Pdf.Table;

namespace Zorg.Pdf.Markdown;

/// <summary>
/// Turns Markdown (CommonMark, with GitHub's tables, task lists, strikethrough and autolinks) into a Zorg.Pdf
/// document: headings, paragraphs with bold and italic, lists, tables, code, quotes, images and links, all tagged as
/// usual. Images become data slots named by their path, so nothing is read from disk here; links become
/// <see cref="Pdf.Link"/> inlines. Raw HTML is dropped with a warning. Parsing is by Markdig.
/// </summary>
/// <example>
/// <code>
/// MarkdownResult result = MarkdownDocument.Parse("# Minutes\n\nPresent: **Kari**, Ola.\n\n- Budget\n- Hiring", new MarkdownOptions { Language = "nb" });
/// await result.Document.RenderAsync(output, new RenderOptions { Fonts = fonts, Data = PdfData.Empty.WithImage("logo.png", logo) });
/// </code>
/// </example>
public static class MarkdownDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UseYamlFrontMatter()
        .Build();

    /// <summary>Converts Markdown to a document, with notes on anything it simplified or dropped.</summary>
    /// <param name="markdown">The Markdown text.</param>
    /// <param name="options">Title, language and styles; null for the defaults.</param>
    /// <returns>The document and the diagnostics, each with a <c>markdown:line:column</c> path.</returns>
    public static MarkdownResult Parse(string markdown, MarkdownOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= MarkdownOptions.Default;
        Markdig.Syntax.MarkdownDocument parsed;
        try
        {
            parsed = Markdig.Markdown.Parse(markdown, Pipeline);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("nested", StringComparison.OrdinalIgnoreCase))
        {
            // Markdig stops at its depth limit (lists, quotes, emphasis or links inside each other) by throwing.
            var empty = new PdfDocument(options.Title ?? "Untitled") { Language = options.Language ?? "en", Styles = options.Styles };
            return new MarkdownResult(empty, [new PdfDiagnostic("ZPDF-M004", DiagnosticSeverity.Error, "The Markdown is nested too deeply (lists, quotes, emphasis or links inside each other) to read.", "markdown")]);
        }
        var converter = new Converter();
        (string? title, string? language) = converter.FrontMatter(parsed);
        ImmutableArray<Block> content = converter.Blocks(parsed);
        string documentTitle = options.Title ?? title ?? content.OfType<Heading>().Where(h => h.Level == 1).Select(h => PlainText(h.Inlines)).FirstOrDefault(t => t.Length > 0) ?? "Untitled";
        var document = new PdfDocument(documentTitle)
        {
            Language = options.Language ?? language ?? "en",
            Styles = options.Styles,
            Content = content,
        };
        return new MarkdownResult(document, [.. converter.Diagnostics]) { Images = [.. converter.Images.Distinct(StringComparer.Ordinal)] };
    }

    private static string PlainText(ImmutableArray<Inline> inlines) =>
        string.Concat(inlines.Select(i => i switch
        {
            TextRun run => run.Text,
            PdfLink link => PlainText(link.Inlines),
            LineBreak => " ",
            _ => "",
        })).Trim();

    /// <summary>One conversion: the diagnostics so far.</summary>
    private sealed class Converter
    {
        public List<PdfDiagnostic> Diagnostics { get; } = [];

        public List<string> Images { get; } = [];

        /// <summary>Reads <c>title:</c> and <c>lang:</c> from YAML front matter, as plain <c>key: value</c> lines.</summary>
        public (string? Title, string? Language) FrontMatter(Markdig.Syntax.MarkdownDocument parsed)
        {
            if (parsed.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is not { } yaml)
            {
                return (null, null);
            }

            string? title = null, language = null;
            foreach (string line in yaml.Lines.ToString().Split('\n'))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "title":
                        title = value;
                        break;
                    case "lang" or "language":
                        language = value;
                        break;
                    default:
                        Note(DiagnosticSeverity.Info, "ZPDF-M002", $"The front matter's \"{key}\" isn't used; only title and lang are.", yaml);
                        break;
                }
            }

            return (title, language);
        }

        public ImmutableArray<Block> Blocks(ContainerBlock container)
        {
            var blocks = ImmutableArray.CreateBuilder<Block>();
            foreach (Markdig.Syntax.Block block in container)
            {
                Add(block, blocks, style: null);
            }

            return blocks.ToImmutable();
        }

        private void Add(Markdig.Syntax.Block block, ImmutableArray<Block>.Builder output, string? style)
        {
            switch (block)
            {
                case YamlFrontMatterBlock or LinkReferenceDefinitionGroup or BlankLineBlock:
                    break;
                case HeadingBlock heading:
                    output.Add(new Heading(Inlines(heading.Inline)) { Level = Math.Clamp(heading.Level, 1, 6) });
                    break;
                case ParagraphBlock paragraph when SoleImage(paragraph) is { } image:
                    output.Add(image);
                    break;
                case ParagraphBlock paragraph:
                    output.Add(new Paragraph(Inlines(paragraph.Inline)) { Style = style });
                    break;
                case ListBlock list:
                    output.Add(List(list));
                    break;
                case QuoteBlock quote:
                    foreach (Markdig.Syntax.Block inner in quote)
                    {
                        Add(inner, output, "quote");
                    }

                    break;
                case CodeBlock code:
                    string text = code.Lines.ToString().Replace("\t", "    ", StringComparison.Ordinal).TrimEnd('\n', '\r');
                    output.Add(new Paragraph([new TextRun(text)]) { Style = "code" });
                    break;
                case ThematicBreakBlock:
                    output.Add(new Spacer(Length.Pt(12)));
                    break;
                case MdTable table:
                    output.Add(Table(table));
                    break;
                case HtmlBlock html:
                    Note(DiagnosticSeverity.Warning, "ZPDF-M001", "Raw HTML isn't rendered; it was left out.", html);
                    break;
                case ContainerBlock other:
                    foreach (Markdig.Syntax.Block inner in other)
                    {
                        Add(inner, output, style);
                    }

                    break;
                default:
                    Note(DiagnosticSeverity.Info, "ZPDF-M002", $"A {block.GetType().Name} isn't supported; it was left out.", block);
                    break;
            }
        }

        private PdfList List(ListBlock list)
        {
            var items = ImmutableArray.CreateBuilder<ListItem>();
            foreach (ListItemBlock item in list.OfType<ListItemBlock>())
            {
                var content = ImmutableArray.CreateBuilder<Block>();
                foreach (Markdig.Syntax.Block inner in item)
                {
                    var converted = ImmutableArray.CreateBuilder<Block>();
                    Add(inner, converted, style: null);
                    foreach (Block block in converted)
                    {
                        if (block is Paragraph or PdfList)
                        {
                            content.Add(block);
                        }
                        else
                        {
                            Note(DiagnosticSeverity.Info, "ZPDF-M002", "A list item holds paragraphs and lists; a heading, table, image or code block in it is shown as a paragraph, or left out.", inner);
                            if (block is Heading heading)
                            {
                                content.Add(new Paragraph(heading.Inlines));
                            }
                        }
                    }
                }

                items.Add(new ListItem(content.Count > 0 ? content.ToImmutable() : [new Paragraph("")]));
            }

            int start = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out int first) ? first : 1;
            return new PdfList(items.ToImmutable()) { Marker = list.IsOrdered ? ListMarker.Number : ListMarker.Bullet, Start = start };
        }

        private PdfTable Table(MdTable table)
        {
            TableRow? header = table.OfType<TableRow>().FirstOrDefault(r => r.IsHeader);
            int count = table.ColumnDefinitions.Count;
            var columns = ImmutableArray.CreateBuilder<Column>(count);
            for (int c = 0; c < count; c++)
            {
                string title = header is not null && c < header.Count && header[c] is TableCell cell ? PlainText(CellInlines(cell)) : "";
                TextAlign align = table.ColumnDefinitions[c].Alignment switch
                {
                    TableColumnAlign.Center => TextAlign.Center,
                    TableColumnAlign.Right => TextAlign.Right,
                    _ => TextAlign.Left,
                };
                columns.Add(new Column(title) { Align = align });
            }

            var rows = ImmutableArray.CreateBuilder<ImmutableArray<Cell>>();
            foreach (TableRow row in table.OfType<TableRow>().Where(r => !r.IsHeader))
            {
                var cells = ImmutableArray.CreateBuilder<Cell>(count);
                for (int c = 0; c < count; c++)
                {
                    cells.Add(c < row.Count && row[c] is TableCell cell ? new Cell(CellInlines(cell)) : new Cell(""));
                }

                rows.Add(cells.MoveToImmutable());
            }

            return new PdfTable(columns.MoveToImmutable()) { Rows = TableRows.Inline(rows.ToImmutable()) };
        }

        private ImmutableArray<Inline> CellInlines(TableCell cell)
        {
            ParagraphBlock? paragraph = cell.OfType<ParagraphBlock>().FirstOrDefault();
            return paragraph is null ? [] : Strip(Inlines(paragraph.Inline), cell);
        }

        /// <summary>Links can't go in table cells yet: their text stays, with a note.</summary>
        private ImmutableArray<Inline> Strip(ImmutableArray<Inline> inlines, Markdig.Syntax.Block where)
        {
            if (!inlines.Any(i => i is PdfLink))
            {
                return inlines;
            }

            Note(DiagnosticSeverity.Info, "ZPDF-M003", "Links in table cells aren't supported yet; the link's text is kept.", where);
            return [.. inlines.SelectMany(i => i is PdfLink link ? link.Inlines : [i])];
        }

        /// <summary>A paragraph that is just one image becomes an image block, with its description as alt text.</summary>
        private ImageBlock? SoleImage(ParagraphBlock paragraph)
        {
            if (paragraph.Inline is not { } inline)
            {
                return null;
            }

            Markdig.Syntax.Inlines.Inline[] parts = [.. inline.Where(i => i is not LiteralInline { Content.IsEmpty: true } && !(i is LiteralInline literal && string.IsNullOrWhiteSpace(literal.Content.ToString())))];
            if (parts is not [LinkInline { IsImage: true } image] || string.IsNullOrWhiteSpace(image.Url))
            {
                return null;
            }

            Images.Add(image.Url);
            string alt = string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString())).Trim();
            return alt.Length > 0
                ? new ImageBlock(ImageSource.FromSlot(image.Url)) { Alt = alt }
                : new ImageBlock(ImageSource.FromSlot(image.Url)) { Decorative = true };
        }

        private ImmutableArray<Inline> Inlines(ContainerInline? container)
        {
            var inlines = ImmutableArray.CreateBuilder<Inline>();
            if (container is not null)
            {
                Append(container, new TextStyle(), inlines, inLink: false);
            }

            return inlines.ToImmutable();
        }

        private void Append(ContainerInline container, TextStyle style, ImmutableArray<Inline>.Builder output, bool inLink)
        {
            foreach (Markdig.Syntax.Inlines.Inline inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        Text(literal.Content.ToString(), style, output);
                        break;
                    case HtmlEntityInline entity:
                        Text(entity.Transcoded.ToString(), style, output);
                        break;
                    case CodeInline code:
                        Text(code.Content, style, output);
                        break;
                    case LineBreakInline lineBreak:
                        if (lineBreak.IsHard)
                        {
                            output.Add(LineBreak.Value);
                        }
                        else
                        {
                            Text(" ", style, output);
                        }

                        break;
                    case EmphasisInline emphasis:
                        TextStyle inner = emphasis.DelimiterChar == '~'
                            ? Note(style, emphasis)
                            : emphasis.DelimiterCount >= 2 ? style with { Weight = FontWeight.Bold } : style with { FontStyle = FontStyle.Italic };
                        Append(emphasis, inner, output, inLink);
                        break;
                    case TaskList task:
                        Text(task.Checked ? "[x]" : "[ ]", style, output);
                        break;
                    case LinkInline { IsImage: true } image:
                        Note(DiagnosticSeverity.Info, "ZPDF-M002", "An image inside text can't be drawn there; put it on a line of its own. Its description is kept as text.", image);
                        Append(image, style, output, inLink);
                        break;
                    case LinkInline link when !inLink && Target(link.Url) is { } target:
                        var text = ImmutableArray.CreateBuilder<Inline>();
                        Append(link, style, text, inLink: true);
                        output.Add(new PdfLink(text.ToImmutable()) { Target = target, Description = string.IsNullOrWhiteSpace(link.Title) ? null : link.Title });
                        break;
                    case LinkInline link:
                        if (!inLink)
                        {
                            Note(DiagnosticSeverity.Info, "ZPDF-M003", $"\"{link.Url}\" isn't a link target a PDF can use (https://, http://, mailto: or #id); the text is kept.", link);
                        }

                        Append(link, style, output, inLink);
                        break;
                    case AutolinkInline auto when !inLink && Target(auto.IsEmail ? "mailto:" + auto.Url : auto.Url) is { } autoTarget:
                        output.Add(new PdfLink([Run(auto.Url, style)]) { Target = autoTarget });
                        break;
                    case AutolinkInline auto:
                        Text(auto.Url, style, output);
                        break;
                    case HtmlInline html:
                        Note(DiagnosticSeverity.Warning, "ZPDF-M001", "Raw HTML isn't rendered; it was left out.", html);
                        break;
                    case ContainerInline other:
                        Append(other, style, output, inLink);
                        break;
                }
            }
        }

        private TextStyle Note(TextStyle style, EmphasisInline strike)
        {
            Note(DiagnosticSeverity.Info, "ZPDF-M002", "Strikethrough isn't supported yet; the text is shown plain.", strike);
            return style;
        }

        /// <summary>A link target a PDF can use, or null.</summary>
        private static string? Target(string? url) =>
            url is null ? null
            : url.StartsWith('#') && url.Length > 1 ? url
            : Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" or "mailto" ? url
            : null;

        /// <summary>Adds text, joined to the previous run when it has the same style.</summary>
        private static void Text(string text, TextStyle style, ImmutableArray<Inline>.Builder output)
        {
            if (text.Length == 0)
            {
                return;
            }

            if (output.Count > 0 && output[^1] is TextRun last && Equals(last.Style, Normalize(style)))
            {
                output[^1] = last with { Text = last.Text + text };
                return;
            }

            output.Add(Run(text, style));
        }

        private static TextRun Run(string text, TextStyle style) => new(text) { Style = Normalize(style) };

        private static TextStyle? Normalize(TextStyle style) => style == new TextStyle() ? null : style;

        private void Note(DiagnosticSeverity severity, string code, string message, MarkdownObject where) =>
            Diagnostics.Add(new PdfDiagnostic(code, severity, message, string.Create(CultureInfo.InvariantCulture, $"markdown:{where.Line + 1}:{where.Column + 1}")));
    }
}

/// <summary>What <see cref="MarkdownDocument.Parse"/> made, and what it couldn't keep.</summary>
/// <param name="Document">The document.</param>
/// <param name="Diagnostics">Notes and warnings, each with a <c>markdown:line:column</c> path.</param>
public sealed record MarkdownResult(PdfDocument Document, ImmutableArray<PdfDiagnostic> Diagnostics)
{
    /// <summary>The image slots the document uses, which are the images' paths or URLs as written: supply each in <see cref="PdfData"/>.</summary>
    public ImmutableArray<string> Images { get; init; } = [];

    /// <summary>The same as a JSON parse result, for code that takes either, such as the agent tools' <c>ParseMarkdown</c>.</summary>
    /// <returns>The document and the diagnostics.</returns>
    public DocumentParseResult ToParseResult() => new(Document, Diagnostics);
}

/// <summary>How Markdown becomes a document.</summary>
/// <example>
/// <code>
/// var options = new MarkdownOptions { Title = "Minutes 14 November", Language = "nb" };
/// </code>
/// </example>
public sealed record MarkdownOptions
{
    /// <summary>The defaults: title from front matter or the first level 1 heading, language from front matter or English.</summary>
    public static MarkdownOptions Default { get; } = new();

    /// <summary>The document's title. Null: the front matter's <c>title:</c>, else the first level 1 heading, else "Untitled".</summary>
    public string? Title { get; init; }

    /// <summary>The document's language (BCP 47). Null: the front matter's <c>lang:</c>, else <c>en</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Styles for the document, such as <c>code</c> with a monospace family and <c>quote</c>. Null: the defaults.</summary>
    public StyleSheet? Styles { get; init; }
}
