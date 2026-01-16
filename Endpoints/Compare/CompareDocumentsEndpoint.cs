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
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

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
    /// Fixes relationship issues using ZipArchive: converts GUID-style IDs to rIdN format,
    /// absolute paths to relative paths, and updates all r:* references in content parts
    /// </summary>
    private static byte[] FixRelationshipsWithZip(byte[] docBytes)
    {
        using var inputStream = new MemoryStream(docBytes);
        using var outputStream = new MemoryStream();

        // First pass: read all entries and build ID mappings from .rels files
        // Key: .rels file path, Value: dictionary of oldId -> newId
        var idMappings = new Dictionary<string, Dictionary<string, string>>();
        var entryContents = new Dictionary<string, byte[]>();

        using (var archive = new ZipArchive(inputStream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                using var entryStream = entry.Open();
                using var memStream = new MemoryStream();
                entryStream.CopyTo(memStream);
                entryContents[entry.FullName] = memStream.ToArray();

                // Build mappings for .rels files
                if (entry.FullName.EndsWith(".rels"))
                {
                    memStream.Position = 0;
                    var xdoc = XDocument.Load(memStream);
                    var mapping = BuildIdMapping(xdoc);
                    if (mapping.Count > 0)
                    {
                        idMappings[entry.FullName] = mapping;
                    }
                }
            }
        }

        // Second pass: write all entries, applying fixes
        using (var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, true))
        {
            foreach (var kvp in entryContents)
            {
                var entryPath = kvp.Key;
                var content = kvp.Value;

                var newEntry = outputArchive.CreateEntry(entryPath, CompressionLevel.Optimal);
                using var newEntryStream = newEntry.Open();

                if (entryPath.EndsWith(".rels"))
                {
                    // Fix .rels file (rename IDs and fix paths)
                    using var contentStream = new MemoryStream(content);
                    var xdoc = XDocument.Load(contentStream);
                    FixRelationshipsXml(xdoc, entryPath, idMappings.GetValueOrDefault(entryPath));
                    xdoc.Save(newEntryStream);
                }
                else if (IsContentPartWithRelationships(entryPath))
                {
                    // Find the corresponding .rels file for this content part
                    var relsPath = GetRelsPathForContentPart(entryPath);
                    var mapping = idMappings.GetValueOrDefault(relsPath);

                    if (mapping != null && mapping.Count > 0)
                    {
                        // Update r:* references in the content part
                        using var contentStream = new MemoryStream(content);
                        var xdoc = XDocument.Load(contentStream);
                        UpdateRelationshipReferences(xdoc, mapping);
                        xdoc.Save(newEntryStream);
                    }
                    else
                    {
                        newEntryStream.Write(content, 0, content.Length);
                    }
                }
                else
                {
                    newEntryStream.Write(content, 0, content.Length);
                }
            }
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Builds a mapping of old IDs to new IDs for GUID-style relationship IDs
    /// </summary>
    private static Dictionary<string, string> BuildIdMapping(XDocument relsDoc)
    {
        var mapping = new Dictionary<string, string>();

        if (relsDoc.Root == null)
            return mapping;

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

        // Build mapping for GUID-style IDs
        foreach (var rel in relationships)
        {
            var idAttr = rel.Attribute("Id");
            if (idAttr == null) continue;

            if (!idAttr.Value.StartsWith("rId"))
            {
                var oldId = idAttr.Value;
                var newId = $"rId{nextId++}";
                mapping[oldId] = newId;
            }
        }

        return mapping;
    }

    /// <summary>
    /// Fixes the relationships XML document using the pre-built mapping
    /// </summary>
    /// <param name="relsDoc">The relationships XML document</param>
    /// <param name="relsPath">The path of the .rels file (e.g., "_rels/.rels" or "word/_rels/document.xml.rels")</param>
    /// <param name="idMapping">Mapping of old relationship IDs to new IDs</param>
    private static void FixRelationshipsXml(XDocument relsDoc, string relsPath, Dictionary<string, string>? idMapping)
    {
        if (relsDoc.Root == null)
            return;

        // Compute the base folder for this .rels file
        // e.g., "_rels/.rels" -> "" (root), "word/_rels/document.xml.rels" -> "word/"
        var baseFolder = GetBaseFolderForRelsFile(relsPath);

        var relationships = relsDoc.Root.Elements(Rel + "Relationship").ToList();

        foreach (var rel in relationships)
        {
            var idAttr = rel.Attribute("Id");
            var targetAttr = rel.Attribute("Target");

            if (idAttr == null) continue;

            // Apply ID mapping if exists
            if (idMapping != null && idMapping.TryGetValue(idAttr.Value, out var newId))
            {
                idAttr.Value = newId;
            }

            // Fix absolute paths - convert to relative paths based on .rels location
            if (targetAttr != null && targetAttr.Value.StartsWith("/"))
            {
                targetAttr.Value = NormalizeTargetPath(targetAttr.Value, baseFolder);
            }
        }
    }

    /// <summary>
    /// Gets the base folder for a .rels file (the folder containing the parts it references)
    /// </summary>
    /// <param name="relsPath">Path like "_rels/.rels" or "word/_rels/document.xml.rels"</param>
    /// <returns>Base folder like "" or "word/"</returns>
    private static string GetBaseFolderForRelsFile(string relsPath)
    {
        // Remove the filename to get the _rels folder path
        var relsFolder = Path.GetDirectoryName(relsPath)?.Replace('\\', '/') ?? "";

        // The base folder is the parent of the _rels folder
        // "_rels" -> "" (root)
        // "word/_rels" -> "word/"
        if (relsFolder == "_rels")
            return "";

        if (relsFolder.EndsWith("/_rels"))
            return relsFolder[..^5]; // Remove "/_rels", keep trailing context

        return "";
    }

    /// <summary>
    /// Normalizes an absolute target path to be relative to the base folder
    /// </summary>
    /// <param name="absolutePath">Absolute path like "/word/footnotes.xml"</param>
    /// <param name="baseFolder">Base folder like "" or "word/"</param>
    /// <returns>Relative path appropriate for the .rels location</returns>
    private static string NormalizeTargetPath(string absolutePath, string baseFolder)
    {
        // Remove leading slash to get the path from package root
        var pathFromRoot = absolutePath.TrimStart('/');

        // If baseFolder is empty (root-level .rels), the target is relative to root
        if (string.IsNullOrEmpty(baseFolder))
            return pathFromRoot;

        // If the path starts with the base folder, make it relative to that folder
        // e.g., baseFolder="word/", path="word/footnotes.xml" -> "footnotes.xml"
        if (pathFromRoot.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase))
            return pathFromRoot[baseFolder.Length..];

        // Path is outside the base folder, need to use ../ to navigate up
        // e.g., baseFolder="word/", path="customXml/item1.xml" -> "../customXml/item1.xml"
        return "../" + pathFromRoot;
    }

    /// <summary>
    /// Updates relationship references (r:id, r:embed, r:link, etc.) in a content part
    /// </summary>
    private static void UpdateRelationshipReferences(XDocument contentDoc, Dictionary<string, string> idMapping)
    {
        // Attributes that reference relationship IDs
        var relAttributeNames = new[] { "id", "embed", "link" };

        foreach (var element in contentDoc.Descendants())
        {
            foreach (var attrName in relAttributeNames)
            {
                var attr = element.Attribute(R + attrName);
                if (attr != null && idMapping.TryGetValue(attr.Value, out var newId))
                {
                    attr.Value = newId;
                }
            }
        }
    }

    /// <summary>
    /// Determines if a zip entry path is a content part that may contain relationship references
    /// </summary>
    private static bool IsContentPartWithRelationships(string entryPath)
    {
        return entryPath.StartsWith("word/") &&
               entryPath.EndsWith(".xml") &&
               !entryPath.Contains("/_rels/");
    }

    /// <summary>
    /// Gets the .rels file path for a given content part
    /// </summary>
    private static string GetRelsPathForContentPart(string contentPartPath)
    {
        // e.g., "word/document.xml" -> "word/_rels/document.xml.rels"
        var directory = Path.GetDirectoryName(contentPartPath)?.Replace('\\', '/') ?? "";
        var fileName = Path.GetFileName(contentPartPath);
        return string.IsNullOrEmpty(directory)
            ? $"_rels/{fileName}.rels"
            : $"{directory}/_rels/{fileName}.rels";
    }
}
