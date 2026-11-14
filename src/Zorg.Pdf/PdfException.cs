using System;

namespace Zorg.Pdf;

/// <summary>
/// The base type for every failure the library reports. Callers can catch this one type and know they have
/// caught everything the library throws on purpose.
/// </summary>
/// <example>
/// <code>
/// try
/// {
///     // render a document
/// }
/// catch (PdfException ex)
/// {
///     logger.LogWarning(ex, "The report could not be rendered.");
/// }
/// </code>
/// </example>
public class PdfException : Exception
{
    /// <summary>Creates an exception with a default message.</summary>
    public PdfException()
    {
    }

    /// <summary>Creates an exception with a message that says what went wrong and what to do next.</summary>
    /// <param name="message">The message.</param>
    public PdfException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the exception that caused it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PdfException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
