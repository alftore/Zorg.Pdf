namespace Zorg.Pdf.Validation;

/// <summary>
/// Every diagnostic code. A code never changes meaning; a retired code stays here and in <c>docs/diagnostics.md</c>
/// marked retired. A test fails when a code here isn't documented, or a documented code isn't here.
/// </summary>
/// <remarks>
/// Areas: J JSON and model shape, D data slots, F fonts, T text, L layout and pagination, I images, A accessibility,
/// R rendering and limits.
/// </remarks>
internal static class DiagnosticCodes
{
    // J: JSON and model shape.
    public const string SchemaVersion = "ZPDF-J001";
    public const string UnknownProperty = "ZPDF-J002";
    public const string InvalidJson = "ZPDF-J003";
    public const string MissingProperty = "ZPDF-J004";
    public const string UnknownType = "ZPDF-J005";
    public const string DocumentTooLarge = "ZPDF-J006";
    public const string HeadingLevel = "ZPDF-J010";
    public const string NoColumns = "ZPDF-J011";
    public const string CellCount = "ZPDF-J012";
    public const string BadLength = "ZPDF-J013";
    public const string NoContentArea = "ZPDF-J014";
    public const string UnknownStyle = "ZPDF-J015";
    public const string BadColumnWidth = "ZPDF-J016";
    public const string EmptySlot = "ZPDF-J017";
    public const string BandContent = "ZPDF-J018";
    public const string BadLineHeight = "ZPDF-J019";
    public const string ListShape = "ZPDF-J020";
    public const string ListContent = "ZPDF-J021";
    public const string BadWatermark = "ZPDF-J022";
    public const string BadTemplate = "ZPDF-J023";
    public const string BadTextBox = "ZPDF-J024";
    public const string BadId = "ZPDF-J025";
    public const string BadOutline = "ZPDF-J026";

    // B: binding a template to its model.
    public const string BindingMissing = "ZPDF-B001";
    public const string RepeatNotArray = "ZPDF-B002";

    // D: data slots.
    public const string MissingSlot = "ZPDF-D001";

    // A: accessibility.
    public const string NoTitle = "ZPDF-A001";
    public const string BadLanguage = "ZPDF-A002";
    public const string NoAltText = "ZPDF-A003";
    public const string HeadingSkipsLevel = "ZPDF-A004";

    // F: fonts.
    public const string NoFonts = "ZPDF-F001";
    public const string MissingGlyph = "ZPDF-F002";
    public const string MissingFace = "ZPDF-F003";
    public const string UnknownFontFamily = "ZPDF-F004";
    public const string FontUnreadable = "ZPDF-F005";

    // T: text.
    public const string WordTooWide = "ZPDF-T001";
    public const string ControlCharacter = "ZPDF-T002";

    // L: layout and pagination.
    public const string RowTooTall = "ZPDF-L001";
    public const string RowCellCount = "ZPDF-L002";
    public const string ColumnsTooWide = "ZPDF-L003";
    public const string BandsTooTall = "ZPDF-L004";
    public const string TextShrunk = "ZPDF-L005";
    public const string TextOverflow = "ZPDF-L006";
    public const string TextBoxOffPage = "ZPDF-L007";

    // I: images.
    public const string ImageUnreadable = "ZPDF-I001";
    public const string ImageTooLarge = "ZPDF-I002";

    // R: rendering and limits.
    public const string NotDrawnYet = "ZPDF-R001";
    public const string AsyncRowsInSyncRender = "ZPDF-R002";
    public const string LimitReached = "ZPDF-R003";
}
