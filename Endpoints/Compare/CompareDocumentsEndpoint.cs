using FastEndpoints;
using Docxodus;
using DocumentFormat.OpenXml.Packaging;
using System.Xml.Linq;

namespace RedlineApi.Endpoints.Compare;

public class CompareRequest
{
    public IFormFile Original { get; set; } = null!;
    public IFormFile Modified { get; set; } = null!;
    public string? Author { get; set; }
}

public class CompareDocumentsEndpoint : Endpoint<CompareRequest>
{
    // PowerTools namespace that causes Word warnings
    private static readonly XNamespace Pt14 = "http://powertools.codeplex.com/2011";

    public override void Configure()
    {
        Post("/api/compare");
        AllowFileUploads();
        AllowAnonymous(); // Remove this line to require authentication
    }

    public override async Task HandleAsync(CompareRequest req, CancellationToken ct)
    {
        // Validate files are DOCX
        if (!req.Original.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
            !req.Modified.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            AddError("Both files must be .docx format");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        try
        {
            // Read files into byte arrays
            using var originalStream = new MemoryStream();
            using var modifiedStream = new MemoryStream();

            await req.Original.CopyToAsync(originalStream, ct);
            await req.Modified.CopyToAsync(modifiedStream, ct);

            // Create WmlDocuments from byte arrays
            var originalDoc = new WmlDocument("original.docx", originalStream.ToArray());
            var modifiedDoc = new WmlDocument("modified.docx", modifiedStream.ToArray());

            // Configure comparison settings
            var settings = new WmlComparerSettings
            {
                AuthorForRevisions = req.Author ?? "Redline API",
                DetailThreshold = 0
            };

            // Perform comparison
            var result = WmlComparer.Compare(originalDoc, modifiedDoc, settings);

            // Clean the output to remove PowerTools internal attributes
            var cleanedBytes = CleanPowerToolsNamespace(result.DocumentByteArray);

            // Return the redlined document
            await Send.BytesAsync(
                cleanedBytes,
                fileName: "redlined.docx",
                contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                cancellation: ct
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error comparing documents");
            AddError($"Failed to compare documents: {ex.Message}");
            await Send.ErrorsAsync(statusCode: 500, cancellation: ct);
        }
    }

    /// <summary>
    /// Removes PowerTools pt14 namespace and attributes from the document
    /// to prevent Word "unreadable content" warnings
    /// </summary>
    private static byte[] CleanPowerToolsNamespace(byte[] docBytes)
    {
        using var stream = new MemoryStream();
        stream.Write(docBytes, 0, docBytes.Length);
        stream.Position = 0;

        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            // Clean main document part
            if (doc.MainDocumentPart != null)
            {
                CleanPart(doc.MainDocumentPart);

                // Clean styles part
                if (doc.MainDocumentPart.StyleDefinitionsPart != null)
                    CleanPart(doc.MainDocumentPart.StyleDefinitionsPart);

                // Clean numbering part
                if (doc.MainDocumentPart.NumberingDefinitionsPart != null)
                    CleanPart(doc.MainDocumentPart.NumberingDefinitionsPart);

                // Clean settings part
                if (doc.MainDocumentPart.DocumentSettingsPart != null)
                    CleanPart(doc.MainDocumentPart.DocumentSettingsPart);

                // Clean footnotes
                if (doc.MainDocumentPart.FootnotesPart != null)
                    CleanPart(doc.MainDocumentPart.FootnotesPart);

                // Clean endnotes
                if (doc.MainDocumentPart.EndnotesPart != null)
                    CleanPart(doc.MainDocumentPart.EndnotesPart);

                // Clean headers
                foreach (var headerPart in doc.MainDocumentPart.HeaderParts)
                    CleanPart(headerPart);

                // Clean footers
                foreach (var footerPart in doc.MainDocumentPart.FooterParts)
                    CleanPart(footerPart);
            }
        }

        return stream.ToArray();
    }

    private static void CleanPart(OpenXmlPart part)
    {
        using var partStream = part.GetStream(FileMode.Open, FileAccess.ReadWrite);
        var xdoc = XDocument.Load(partStream);

        // Remove all pt14 attributes from all elements
        foreach (var element in xdoc.Descendants())
        {
            var pt14Attrs = element.Attributes()
                .Where(a => a.Name.Namespace == Pt14)
                .ToList();

            foreach (var attr in pt14Attrs)
                attr.Remove();
        }

        // Remove pt14 namespace declaration from root
        var root = xdoc.Root;
        if (root != null)
        {
            var nsDeclarations = root.Attributes()
                .Where(a => a.IsNamespaceDeclaration && a.Value == Pt14.NamespaceName)
                .ToList();

            foreach (var ns in nsDeclarations)
                ns.Remove();

            // Also remove pt14 from mc:Ignorable attribute if present
            var mcIgnorable = root.Attribute(XName.Get("Ignorable", "http://schemas.openxmlformats.org/markup-compatibility/2006"));
            if (mcIgnorable != null)
            {
                var values = mcIgnorable.Value.Split(' ')
                    .Where(v => v != "pt14")
                    .ToArray();
                mcIgnorable.Value = string.Join(" ", values);
            }
        }

        // Save back
        partStream.SetLength(0);
        xdoc.Save(partStream);
    }
}
