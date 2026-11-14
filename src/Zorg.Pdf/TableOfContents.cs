using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Zorg.Pdf;

/// <summary>
/// A table of contents: one entry per heading of the document (top-level headings, levels 1 to
/// <see cref="MaxLevel"/>), indented by level, with dot leaders and the page each heading lands on. Every entry links
/// to its heading. It's laid out where it stands and its page numbers are filled in when the document is finished,
/// so it can come before the headings it lists. Entries use the styles <c>toc1</c> to <c>toc6</c> (<c>toc1</c> is
/// bold) over <c>body</c>. In JSON <c>{ "type": "tableOfContents", "title": "Contents", "maxLevel": 2 }</c>.
/// </summary>
/// <example>
/// <code>
/// Content = [new Heading("Annual report 2026"), new TableOfContents { Title = "Contents", MaxLevel = 2 }, new PageBreak(), ...]
/// </code>
/// </example>
public sealed record TableOfContents : Block
{
    /// <summary>A table of contents with the defaults: headings of levels 1 to 3, with leaders, no title.</summary>
    public TableOfContents()
    {
    }

    /// <summary>
    /// For the JSON serializer only. Every JSON property is a parameter, so a property the JSON leaves out keeps its
    /// default: source generation can't set init-only properties one by one.
    /// </summary>
    [JsonConstructor]
    [SetsRequiredMembers]
    internal TableOfContents(int maxLevel = 3, string? title = null, bool leaders = true, string? style = null, bool keepWithNext = false, string? id = null) =>
        (MaxLevel, Title, Leaders, Style, KeepWithNext, Id) = (maxLevel, title, leaders, style, keepWithNext, id);

    /// <summary>The deepest heading level listed: 3 by default, from 1 to 6.</summary>
    public int MaxLevel { get; init; } = 3;

    /// <summary>A heading drawn above the entries, such as "Contents". It isn't an entry itself.</summary>
    public string? Title { get; init; }

    /// <summary>Dots from each entry's text to its page number: on by default.</summary>
    public bool Leaders { get; init; } = true;
}
