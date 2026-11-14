using System;
using System.Collections.Immutable;
using System.IO;
using System.Xml;

namespace Zorg.Pdf.Invoicing;

/// <summary>
/// Factur-X and ZUGFeRD (2.x) e-invoices: a PDF a person reads, with the invoice's Cross Industry Invoice (CII) XML
/// inside it for software to read. Give it the XML your invoicing system makes; it reads the profile from the XML,
/// and <see cref="FacturXInvoice.ApplyTo(PdfDocument)"/> and <see cref="FacturXInvoice.ApplyTo(RenderOptions)"/> add
/// the attachment, the <c>fx:</c> metadata and PDF/A-3 that receivers check. The PDF's content is your document:
/// keep it saying what the XML says.
/// </summary>
/// <example>
/// <code>
/// FacturXInvoice facturX = FacturX.Read(await File.ReadAllBytesAsync("invoice.xml", cancellationToken));
/// await facturX.ApplyTo(invoice).RenderAsync(output, facturX.ApplyTo(new RenderOptions { Fonts = fonts, Conformance = PdfConformance.PdfUA1 }), cancellationToken);
/// </code>
/// </example>
public static class FacturX
{
    /// <summary>The CII namespace of the XML's root element, <c>rsm:CrossIndustryInvoice</c>.</summary>
    public const string CrossIndustryInvoiceNamespace = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";

    private const string Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";

    /// <summary>The largest XML read: 20 MB.</summary>
    public const int MaxXmlBytes = 20_000_000;

    /// <summary>Reads the invoice's CII XML and its profile.</summary>
    /// <param name="xml">The XML, as the invoicing system wrote it. It's embedded unchanged.</param>
    /// <returns>The invoice, ready to apply to a document and its render options.</returns>
    /// <exception cref="PdfDocumentException">The XML isn't well-formed CII, or names a profile that isn't Factur-X's or ZUGFeRD's (ZPDF-X001 to X003).</exception>
    public static FacturXInvoice Read(ReadOnlyMemory<byte> xml)
    {
        if (xml.Length > MaxXmlBytes)
        {
            throw Error("ZPDF-X001", $"The XML is {xml.Length:N0} bytes, over the limit of {MaxXmlBytes:N0}.");
        }

        string? guideline = null, number = null, typeCode = null;
        string? root = null, rootNamespace = null;
        try
        {
            // No DTDs and no external resources: the XML may come from anywhere.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes, IgnoreComments = true };
            using var stream = new MemoryStream(xml.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            string? parent = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (root is null)
                {
                    (root, rootNamespace) = (reader.LocalName, reader.NamespaceURI);
                    continue;
                }

                if (reader.NamespaceURI == Ram && reader.LocalName is "GuidelineSpecifiedDocumentContextParameter" or "ExchangedDocument")
                {
                    parent = reader.LocalName;
                }
                else if (reader.LocalName is "ExchangedDocument" && reader.NamespaceURI == CrossIndustryInvoiceNamespace)
                {
                    parent = "ExchangedDocument";
                }
                else if (reader.NamespaceURI == Ram && reader.LocalName == "ID" && parent == "GuidelineSpecifiedDocumentContextParameter" && guideline is null)
                {
                    guideline = reader.ReadElementContentAsString().Trim();
                    parent = null;
                }
                else if (reader.NamespaceURI == Ram && reader.LocalName == "ID" && parent == "ExchangedDocument" && number is null)
                {
                    number = reader.ReadElementContentAsString().Trim();
                }
                else if (reader.NamespaceURI == Ram && reader.LocalName == "TypeCode" && parent == "ExchangedDocument" && typeCode is null)
                {
                    typeCode = reader.ReadElementContentAsString().Trim();
                }
            }
        }
        catch (XmlException ex)
        {
            throw Error("ZPDF-X001", $"The XML isn't well-formed: {ex.Message}");
        }

        if (root != "CrossIndustryInvoice" || rootNamespace != CrossIndustryInvoiceNamespace)
        {
            throw Error("ZPDF-X002", $"The XML's root is {root ?? "missing"} in {(string.IsNullOrEmpty(rootNamespace) ? "no namespace" : rootNamespace)}, not rsm:CrossIndustryInvoice in {CrossIndustryInvoiceNamespace}. Factur-X and ZUGFeRD 2 use CII; UBL isn't embedded this way.");
        }

        FacturXProfile? profile = guideline switch
        {
            "urn:factur-x.eu:1p0:minimum" => FacturXProfile.Minimum,
            "urn:factur-x.eu:1p0:basicwl" => FacturXProfile.BasicWL,
            "urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic" => FacturXProfile.Basic,
            "urn:cen.eu:en16931:2017" => FacturXProfile.EN16931,
            "urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended" => FacturXProfile.Extended,
            _ when guideline?.StartsWith("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_", StringComparison.Ordinal) == true => FacturXProfile.XRechnung,
            _ => null,
        };
        if (profile is null)
        {
            throw Error("ZPDF-X003", $"The XML's guideline (GuidelineSpecifiedDocumentContextParameter/ID) is \"{guideline ?? "missing"}\", which isn't a Factur-X or ZUGFeRD profile: MINIMUM, BASIC WL, BASIC, EN 16931, EXTENDED or XRECHNUNG.");
        }

        return new FacturXInvoice(xml, profile.Value, number, typeCode);
    }

    private static PdfDocumentException Error(string code, string message) =>
        new([new PdfDiagnostic(code, DiagnosticSeverity.Error, message, "facturx")]);
}

/// <summary>A Factur-X or ZUGFeRD profile: how much of the invoice the XML holds.</summary>
public enum FacturXProfile
{
    /// <summary>MINIMUM: the totals and parties only. Not a full invoice in France or Germany.</summary>
    Minimum,

    /// <summary>BASIC WL: document level, without lines. Not a full invoice in France or Germany.</summary>
    BasicWL,

    /// <summary>BASIC: a subset of EN 16931, with lines.</summary>
    Basic,

    /// <summary>EN 16931 (COMFORT): the European standard's core invoice.</summary>
    EN16931,

    /// <summary>EXTENDED: EN 16931 and more, for complex invoices.</summary>
    Extended,

    /// <summary>XRECHNUNG: Germany's CIUS of EN 16931, as ZUGFeRD carries it.</summary>
    XRechnung,
}

/// <summary>An invoice's CII XML and its profile, read by <see cref="FacturX.Read"/>.</summary>
public sealed class FacturXInvoice
{
    internal FacturXInvoice(ReadOnlyMemory<byte> xml, FacturXProfile profile, string? number, string? typeCode) =>
        (Xml, Profile, InvoiceNumber, TypeCode) = (xml, profile, number, typeCode);

    /// <summary>The XML, unchanged.</summary>
    public ReadOnlyMemory<byte> Xml { get; }

    /// <summary>The profile the XML says it follows.</summary>
    public FacturXProfile Profile { get; }

    /// <summary>The invoice number (ExchangedDocument/ID), or null.</summary>
    public string? InvoiceNumber { get; }

    /// <summary>The document type code, such as 380 (invoice) or 381 (credit note), or null.</summary>
    public string? TypeCode { get; }

    /// <summary>The attachment's file name: <c>xrechnung.xml</c> for XRECHNUNG, else <c>factur-x.xml</c>.</summary>
    public string FileName => Profile == FacturXProfile.XRechnung ? "xrechnung.xml" : "factur-x.xml";

    /// <summary>The profile as the metadata writes it, such as "EN 16931".</summary>
    public string ConformanceLevel => Profile switch
    {
        FacturXProfile.Minimum => "MINIMUM",
        FacturXProfile.BasicWL => "BASIC WL",
        FacturXProfile.Basic => "BASIC",
        FacturXProfile.EN16931 => "EN 16931",
        FacturXProfile.Extended => "EXTENDED",
        _ => "XRECHNUNG",
    };

    /// <summary>
    /// The XML as an attachment: <see cref="AttachmentRelationship.Alternative"/> (the XML is the invoice too) from
    /// BASIC up, <see cref="AttachmentRelationship.Data"/> for MINIMUM and BASIC WL, which aren't full invoices.
    /// </summary>
    public Attachment Attachment => new(FileName, ImageSource.FromBytes(Xml.ToArray()))
    {
        MimeType = "text/xml",
        Relationship = Profile is FacturXProfile.Minimum or FacturXProfile.BasicWL ? AttachmentRelationship.Data : AttachmentRelationship.Alternative,
        Description = "Factur-X/ZUGFeRD invoice data (" + ConformanceLevel + ")",
    };

    /// <summary>The <c>fx:</c> metadata receivers read, with its PDF/A extension schema.</summary>
    public XmpSchema Metadata => new("Factur-X PDFA Extension Schema", "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#", "fx")
    {
        Properties =
        [
            new XmpProperty("DocumentType", "INVOICE") { Description = "The type of the hybrid document in capital letters, e.g. INVOICE or ORDER" },
            new XmpProperty("DocumentFileName", FileName) { Description = "The name of the embedded XML document" },
            new XmpProperty("Version", "1.0") { Description = "The actual version of the standard applying to the embedded XML document" },
            new XmpProperty("ConformanceLevel", ConformanceLevel) { Description = "The conformance level of the embedded XML document" },
        ],
    };

    /// <summary>The document with the XML attached, in place of any attachment of the same name.</summary>
    /// <param name="document">The invoice as people read it.</param>
    /// <returns>The document with the attachment.</returns>
    public PdfDocument ApplyTo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableArray<Attachment> others = document.Attachments.RemoveAll(a => string.Equals(a.Name, FileName, StringComparison.OrdinalIgnoreCase));
        return document with { Attachments = others.Insert(0, Attachment) };
    }

    /// <summary>The options with PDF/A-3 (at the level asked for, else 3b) and the <c>fx:</c> metadata.</summary>
    /// <param name="options">The render options.</param>
    /// <returns>The options for a Factur-X file.</returns>
    public RenderOptions ApplyTo(RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PdfArchive archive = options.Archive switch
        {
            PdfArchive.PdfA2a or PdfArchive.PdfA3a => PdfArchive.PdfA3a,
            PdfArchive.PdfA2u or PdfArchive.PdfA3u => PdfArchive.PdfA3u,
            _ => PdfArchive.PdfA3b,
        };
        return options with { Archive = archive, MetadataSchemas = options.MetadataSchemas.RemoveAll(s => s.Prefix == "fx").Add(Metadata) };
    }
}
