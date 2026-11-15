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
    public const string BadLink = "ZPDF-J027";
    public const string BadTableOfContents = "ZPDF-J028";
    public const string BadSection = "ZPDF-J029";

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
    public const string NoHyphenation = "ZPDF-T003";
    public const string MarkdownHtml = "ZPDF-M001";        // Reported by Zorg.Pdf.Markdown.
    public const string MarkdownSimplified = "ZPDF-M002";
    public const string MarkdownLink = "ZPDF-M003";
    public const string MarkdownTooDeep = "ZPDF-M004";
    public const string FacturXNotXml = "ZPDF-X001";         // Reported by Zorg.Pdf.Invoicing.
    public const string FacturXNotCii = "ZPDF-X002";
    public const string FacturXProfile = "ZPDF-X003";
    public const string ReadRecovered = "ZPDF-P001";       // Reading existing PDFs.
    public const string ReadStreamLength = "ZPDF-P002";
    public const string ReadDamagedStream = "ZPDF-P003";
    public const string ReadPageTree = "ZPDF-P004";
    public const string ReadDecrypted = "ZPDF-P005";
    public const string BadBackground = "ZPDF-J030";
    public const string BadAttachment = "ZPDF-J031";
    public const string BadBarcode = "ZPDF-J032";
    public const string BadChart = "ZPDF-J033";
    public const string AttachmentNeedsPdfA3 = "ZPDF-A005";
    public const string AttachmentTooLarge = "ZPDF-R004";
    public const string BadMetadataSchema = "ZPDF-R005";
    public const string BackgroundUnreadable = "ZPDF-G001";   // Backgrounds from existing PDFs.
    public const string BackgroundPageMissing = "ZPDF-G002";
    public const string BackgroundScaled = "ZPDF-G003";
    public const string BackgroundFontNotEmbedded = "ZPDF-G004";
    public const string BackgroundColour = "ZPDF-G005";
    public const string FilesTagsDropped = "ZPDF-E001";        // Merging and splitting existing PDFs.
    public const string FilesBookmarksDropped = "ZPDF-E002";
    public const string FilesFormFields = "ZPDF-E003";
    public const string FilesLinksDropped = "ZPDF-E004";
    public const string TextShrunk = "ZPDF-L005";
    public const string TextOverflow = "ZPDF-L006";
    public const string TextBoxOffPage = "ZPDF-L007";
    public const string AddressLayout = "ZPDF-L008";
    public const string AddressTypography = "ZPDF-L009";

    // I: images.
    public const string ImageUnreadable = "ZPDF-I001";
    public const string ImageTooLarge = "ZPDF-I002";

    // R: rendering and limits.
    public const string NotDrawnYet = "ZPDF-R001";
    public const string AsyncRowsInSyncRender = "ZPDF-R002";
    public const string LimitReached = "ZPDF-R003";
}
