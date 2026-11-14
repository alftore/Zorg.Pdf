namespace Zorg.Pdf.Writing;

/// <summary>An indirect object number. The writer always uses generation 0.</summary>
internal readonly record struct PdfReference(int Number)
{
    public bool IsValid => Number > 0;
}
