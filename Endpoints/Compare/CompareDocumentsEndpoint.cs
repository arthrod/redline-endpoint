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
    // PowerTools namespace that can cause Word warnings if not properly handled
    private static readonly XNamespace Pt14 = "http://powertools.codeplex.com/2011";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

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

            // Clean the output to remove PowerTools internal namespace/attributes
            // Note: We only clean pt14 namespace. GUID-style relationship IDs are valid per OOXML spec.
            // DO NOT modify relationship IDs without also updating all references in document content!
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
    /// Removes PowerTools (pt14) internal namespace and attributes from the document.
    /// This prevents Word from showing "unreadable content" warnings for unrecognized namespaces.
    /// </summary>
    private static byte[] CleanPowerToolsNamespace(byte[] docBytes)
    {
        using var stream = new MemoryStream();
        stream.Write(docBytes, 0, docBytes.Length);
        stream.Position = 0;

        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            if (doc.MainDocumentPart != null)
            {
                // Clean all XML parts that may contain pt14 attributes
                CleanXmlPart(doc.MainDocumentPart);

                if (doc.MainDocumentPart.StyleDefinitionsPart != null)
                    CleanXmlPart(doc.MainDocumentPart.StyleDefinitionsPart);

                if (doc.MainDocumentPart.NumberingDefinitionsPart != null)
                    CleanXmlPart(doc.MainDocumentPart.NumberingDefinitionsPart);

                if (doc.MainDocumentPart.DocumentSettingsPart != null)
                    CleanXmlPart(doc.MainDocumentPart.DocumentSettingsPart);

                if (doc.MainDocumentPart.FootnotesPart != null)
                    CleanXmlPart(doc.MainDocumentPart.FootnotesPart);

                if (doc.MainDocumentPart.EndnotesPart != null)
                    CleanXmlPart(doc.MainDocumentPart.EndnotesPart);

                foreach (var headerPart in doc.MainDocumentPart.HeaderParts)
                    CleanXmlPart(headerPart);

                foreach (var footerPart in doc.MainDocumentPart.FooterParts)
                    CleanXmlPart(footerPart);
            }
        }

        stream.Position = 0;
        return stream.ToArray();
    }

    /// <summary>
    /// Removes pt14 namespace declarations and attributes from an XML part.
    /// </summary>
    private static void CleanXmlPart(OpenXmlPart part)
    {
        using var partStream = part.GetStream(FileMode.Open, FileAccess.ReadWrite);
        var xdoc = XDocument.Load(partStream);
        var modified = false;

        // Remove all pt14-namespaced attributes from all elements
        foreach (var element in xdoc.Descendants())
        {
            var pt14Attrs = element.Attributes()
                .Where(a => a.Name.Namespace == Pt14)
                .ToList();

            foreach (var attr in pt14Attrs)
            {
                attr.Remove();
                modified = true;
            }
        }

        // Clean up root element
        var root = xdoc.Root;
        if (root != null)
        {
            // Remove pt14 namespace declaration (xmlns:pt14="...")
            var nsDeclarations = root.Attributes()
                .Where(a => a.IsNamespaceDeclaration && a.Value == Pt14.NamespaceName)
                .ToList();

            foreach (var ns in nsDeclarations)
            {
                ns.Remove();
                modified = true;
            }

            // Remove pt14 from mc:Ignorable attribute if present
            var mcIgnorable = root.Attribute(Mc + "Ignorable");
            if (mcIgnorable != null && mcIgnorable.Value.Contains("pt14"))
            {
                var values = mcIgnorable.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => v != "pt14")
                    .ToArray();

                if (values.Length > 0)
                    mcIgnorable.Value = string.Join(" ", values);
                else
                    mcIgnorable.Remove();

                modified = true;
            }
        }

        // Only save if we made changes
        if (modified)
        {
            partStream.SetLength(0);
            xdoc.Save(partStream);
        }
    }
}
