using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using Zorg.Pdf.Json;

namespace Zorg.Pdf;

/// <summary>How serious a diagnostic is. In JSON <c>"error"</c>, <c>"warning"</c> or <c>"info"</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    /// <summary>Worth knowing; the output is as asked.</summary>
    Info,

    /// <summary>The output differs from what was asked, visibly and in a defined way, such as a word broken between characters.</summary>
    Warning,

    /// <summary>The document can't be rendered as it is.</summary>
    Error,
}

/// <summary>
/// One problem found in a document, with a stable code (listed in <c>docs/diagnostics.md</c>) and a JSON pointer to
/// where it is, such as <c>/content/2/columns/1/width</c>. The pointer means the same for a document built in C#.
/// </summary>
/// <param name="Code">The stable code, such as <c>ZPDF-J002</c>. A code never changes meaning.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What's wrong and how to fix it.</param>
/// <param name="Path">A JSON pointer (RFC 6901) into the document; empty for the document itself.</param>
public sealed record PdfDiagnostic(string Code, DiagnosticSeverity Severity, string Message, string Path)
{
    /// <summary>The page it happened on, from 1, for problems found while drawing pages.</summary>
    public int? Page { get; init; }

    /// <summary>The diagnostic as one line: <c>error ZPDF-J002 at /content/0/colour: ...</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        $"{Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" }} {Code} at {(Path.Length == 0 ? "/" : Path)}: {Message}";
}

/// <summary>The result of <see cref="PdfDocument.Validate"/>.</summary>
public sealed class ValidationResult
{
    internal ValidationResult(ImmutableArray<PdfDiagnostic> diagnostics) => Diagnostics = diagnostics;

    /// <summary>True when there's no diagnostic of <see cref="DiagnosticSeverity.Error"/> severity.</summary>
    public bool IsValid => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Everything found, in document order.</summary>
    public ImmutableArray<PdfDiagnostic> Diagnostics { get; }
}

/// <summary>The result of <see cref="PdfDocument.TryParseJson(string)"/>: a document, or why there isn't one.</summary>
public sealed class DocumentParseResult
{
    internal DocumentParseResult(PdfDocument? document, ImmutableArray<PdfDiagnostic> diagnostics) =>
        (Document, Diagnostics) = (document, diagnostics);

    /// <summary>True when the JSON was a document. It may still fail <see cref="PdfDocument.Validate"/>.</summary>
    [MemberNotNullWhen(true, nameof(Document))]
    public bool IsSuccess => Document is not null;

    /// <summary>The document, when parsing succeeded.</summary>
    public PdfDocument? Document { get; }

    /// <summary>Why parsing failed, with JSON pointers. Empty on success.</summary>
    public ImmutableArray<PdfDiagnostic> Diagnostics { get; }
}

/// <summary>A document couldn't be parsed or isn't valid. <see cref="Diagnostics"/> has every problem found.</summary>
public class PdfDocumentException : PdfException
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfDocumentException() => Diagnostics = [];

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PdfDocumentException(string message)
        : base(message) => Diagnostics = [];

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PdfDocumentException(string message, System.Exception innerException)
        : base(message, innerException) => Diagnostics = [];

    /// <summary>Creates the exception for a set of diagnostics; the message lists them.</summary>
    /// <param name="diagnostics">The problems found.</param>
    public PdfDocumentException(ImmutableArray<PdfDiagnostic> diagnostics)
        : base(Describe(diagnostics)) => Diagnostics = diagnostics;

    /// <summary>Every problem found.</summary>
    public ImmutableArray<PdfDiagnostic> Diagnostics { get; }

    private static string Describe(ImmutableArray<PdfDiagnostic> diagnostics) =>
        diagnostics.IsDefaultOrEmpty
            ? "The document isn't valid."
            : $"The document isn't valid ({diagnostics.Length} problem{(diagnostics.Length == 1 ? "" : "s")}):\n" + string.Join('\n', diagnostics.Select(d => "  " + d));
}
