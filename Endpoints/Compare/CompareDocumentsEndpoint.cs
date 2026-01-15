using FastEndpoints;
using Docxodus;
using DocumentFormat.OpenXml.Packaging;
using System.IO.Compression;
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
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

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

            // Clean the output to remove PowerTools internal attributes and fix relationships
            var cleanedBytes = CleanDocument(result.DocumentByteArray);

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
    /// Cleans the document to remove PowerTools internal attributes and fix relationship issues
    /// </summary>
    private static byte[] CleanDocument(byte[] docBytes)
    {
        using var stream = new MemoryStream();
        stream.Write(docBytes, 0, docBytes.Length);
        stream.Position = 0;

        // First pass: Clean XML parts using OpenXML SDK
        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            if (doc.MainDocumentPart != null)
            {
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

        // Second pass: Fix relationships using ZipArchive
        stream.Position = 0;
        var fixedBytes = FixRelationshipsWithZip(stream.ToArray());

        return fixedBytes;
    }

    /// <summary>
    /// Removes pt14 namespace and attributes from an XML part
    /// </summary>
    private static void CleanXmlPart(OpenXmlPart part)
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

    /// <summary>
    /// Fixes relationship issues using ZipArchive: converts GUID-style IDs to rIdN format
    /// and absolute paths to relative paths
    /// </summary>
    private static byte[] FixRelationshipsWithZip(byte[] docBytes)
    {
        using var inputStream = new MemoryStream(docBytes);
        using var outputStream = new MemoryStream();

        using (var archive = new ZipArchive(inputStream, ZipArchiveMode.Read))
        using (var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, true))
        {
            foreach (var entry in archive.Entries)
            {
                var newEntry = outputArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var newEntryStream = newEntry.Open();

                // Fix relationship files
                if (entry.FullName.EndsWith(".rels"))
                {
                    var xdoc = XDocument.Load(entryStream);
                    FixRelationshipsXml(xdoc);
                    xdoc.Save(newEntryStream);
                }
                else
                {
                    entryStream.CopyTo(newEntryStream);
                }
            }
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Fixes the relationships XML document
    /// </summary>
    private static void FixRelationshipsXml(XDocument relsDoc)
    {
        if (relsDoc.Root == null)
            return;

        var relationships = relsDoc.Root.Elements(Rel + "Relationship").ToList();
        int nextId = 1;

        // Find the highest existing rId number
        foreach (var rel in relationships)
        {
            var id = rel.Attribute("Id")?.Value;
            if (id != null && id.StartsWith("rId") && int.TryParse(id[3..], out int num))
            {
                if (num >= nextId)
                    nextId = num + 1;
            }
        }

        foreach (var rel in relationships)
        {
            var idAttr = rel.Attribute("Id");
            var targetAttr = rel.Attribute("Target");

            if (idAttr == null) continue;

            // Fix GUID-style IDs (like R76e2e2db410345d5)
            if (!idAttr.Value.StartsWith("rId"))
            {
                idAttr.Value = $"rId{nextId++}";
            }

            // Fix absolute paths (like /word/footnotes.xml -> footnotes.xml)
            if (targetAttr != null && targetAttr.Value.StartsWith("/word/"))
            {
                targetAttr.Value = targetAttr.Value[6..]; // Remove "/word/"
            }
        }
    }
}
