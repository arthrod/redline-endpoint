using FastEndpoints;
using Docxodus;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
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
    }

    public override async Task HandleAsync(CompareRequest req, CancellationToken ct)
    {
        // Validate RapidAPI Proxy Secret in production
        var expectedSecret = Environment.GetEnvironmentVariable("RAPIDAPI_PROXY_SECRET");
        if (!string.IsNullOrEmpty(expectedSecret))
        {
            var proxySecret = HttpContext.Request.Headers["X-RapidAPI-Proxy-Secret"].FirstOrDefault();
            if (proxySecret != expectedSecret)
            {
                HttpContext.Response.StatusCode = 401;
                await HttpContext.Response.WriteAsJsonAsync(new { message = "Unauthorized" }, ct);
                return;
            }
        }

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
                AuthorForRevisions = req.Author ?? "User",
                DetailThreshold = 0,
                SimplifyMoveMarkup = true
            };

            // Perform comparison
            var result = WmlComparer.Compare(originalDoc, modifiedDoc, settings);

            // Clean the output to remove PowerTools internal attributes and fix relationships
            var cleanedBytes = CleanDocument(result.DocumentByteArray, message => Logger.LogInformation(message));

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
    private static byte[] CleanDocument(byte[] docBytes, Action<string>? log = null)
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

                FixNotesParts(doc, log);
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
                var values = mcIgnorable.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => v != "pt14")
                    .ToArray();

                if (values.Length == 0)
                    mcIgnorable.Remove();
                else
                    mcIgnorable.Value = string.Join(" ", values);
            }
        }

        // Fix common schema URI bug for images:
        // Word expects "http://schemas.openxmlformats.org/drawingml/2006/picture"
        // (NOT https) in a:graphicData/@uri
        foreach (var gd in xdoc.Descendants().Where(e => e.Name.LocalName == "graphicData"))
        {
            var uriAttr = gd.Attribute("uri");
            if (uriAttr == null) continue;

            if (uriAttr.Value == "https://schemas.openxmlformats.org/drawingml/2006/picture")
                uriAttr.Value = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        }

        // Save back with Word-compatible XML format
        partStream.SetLength(0);
        SaveXDocumentForWord(xdoc, partStream);
    }

    /// <summary>
    /// Saves XDocument with Word-compatible XML formatting:
    /// - XML declaration: encoding="UTF-8" standalone="yes"
    /// - Proper UTF-8 encoding (uppercase in declaration)
    /// </summary>
    private static void SaveXDocumentForWord(XDocument xdoc, Stream stream)
    {
        // Word requires:
        // 1. Uppercase "UTF-8" and standalone="yes" in XML declaration
        // 2. No space before /> in self-closing tags (Word writes <w:jc/>, not <w:jc />)
        var encoding = new UTF8Encoding(false); // UTF-8 without BOM

        // First, write to a temporary buffer
        using var tempStream = new MemoryStream();

        // Write the rest of the document without declaration
        var settings = new XmlWriterSettings
        {
            Encoding = encoding,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            OmitXmlDeclaration = true
        };

        using (var writer = XmlWriter.Create(tempStream, settings))
        {
            xdoc.Save(writer);
        }

        // Get the XML content and fix self-closing tags (only empty-element tags)
        var xmlContent = encoding.GetString(tempStream.ToArray());
        xmlContent = Regex.Replace(
            xmlContent,
            @"<([^>]+?)\s/>",
            "<$1/>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Write the XML declaration manually with uppercase UTF-8
        var declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";
        var declBytes = encoding.GetBytes(declaration);
        stream.Write(declBytes, 0, declBytes.Length);

        // Write the fixed content
        var contentBytes = encoding.GetBytes(xmlContent);
        stream.Write(contentBytes, 0, contentBytes.Length);
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
                    SaveXDocumentForWord(xdoc, newEntryStream);
                }
                else if (IsContentPartWithRelationships(entryPath))
                {
                    // ALWAYS rewrite word/*.xml files with proper XML declaration
                    // Word requires encoding="UTF-8" (uppercase) and standalone="yes"
                    using var contentStream = new MemoryStream(content);
                    var xdoc = XDocument.Load(contentStream);

                    // Find the corresponding .rels file for this content part
                    var relsPath = GetRelsPathForContentPart(entryPath);
                    var mapping = idMappings.GetValueOrDefault(relsPath);

                    if (mapping != null && mapping.Count > 0)
                    {
                        // Update r:* references in the content part
                        UpdateRelationshipReferences(xdoc, mapping);
                    }

                    // Always save with Word-compatible XML format
                    SaveXDocumentForWord(xdoc, newEntryStream);
                }
                else
                {
                    newEntryStream.Write(content, 0, content.Length);
                }
            }
        }

        return outputStream.ToArray();
    }

    private static void FixNotesParts(WordprocessingDocument doc, Action<string>? log)
    {
        if (doc.MainDocumentPart == null)
            return;

        var mainPart = doc.MainDocumentPart;
        var footnoteRefCount = CountFootnoteReferences(mainPart);
        var endnoteRefCount = CountEndnoteReferences(mainPart);
        var hasFootnoteRefs = footnoteRefCount > 0;
        var hasEndnoteRefs = endnoteRefCount > 0;

        log?.Invoke($"Footnote references: {footnoteRefCount}. Endnote references: {endnoteRefCount}.");

        if (!hasFootnoteRefs && mainPart.FootnotesPart != null)
        {
            log?.Invoke("Removing footnotes part (no references found).");
            mainPart.DeletePart(mainPart.FootnotesPart);
        }
        else if (mainPart.FootnotesPart != null)
        {
            EnsureFootnotesHaveSeparators(mainPart.FootnotesPart, log);
        }

        if (!hasEndnoteRefs && mainPart.EndnotesPart != null)
        {
            log?.Invoke("Removing endnotes part (no references found).");
            mainPart.DeletePart(mainPart.EndnotesPart);
        }
        else if (mainPart.EndnotesPart != null)
        {
            EnsureEndnotesHaveSeparators(mainPart.EndnotesPart, log);
        }
    }

    private static int CountFootnoteReferences(MainDocumentPart mainPart)
    {
        var count = mainPart.Document?.Descendants<FootnoteReference>().Count() ?? 0;

        foreach (var headerPart in mainPart.HeaderParts)
        {
            count += headerPart.Header?.Descendants<FootnoteReference>().Count() ?? 0;
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            count += footerPart.Footer?.Descendants<FootnoteReference>().Count() ?? 0;
        }

        return count;
    }

    private static int CountEndnoteReferences(MainDocumentPart mainPart)
    {
        var count = mainPart.Document?.Descendants<EndnoteReference>().Count() ?? 0;

        foreach (var headerPart in mainPart.HeaderParts)
        {
            count += headerPart.Header?.Descendants<EndnoteReference>().Count() ?? 0;
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            count += footerPart.Footer?.Descendants<EndnoteReference>().Count() ?? 0;
        }

        return count;
    }

    private static void EnsureFootnotesHaveSeparators(FootnotesPart footnotesPart, Action<string>? log)
    {
        var footnotes = footnotesPart.Footnotes ?? new Footnotes();

        if (footnotesPart.Footnotes == null)
            footnotesPart.Footnotes = footnotes;

        var separator = footnotes.Elements<Footnote>()
            .FirstOrDefault(f => f.Type?.Value == FootnoteEndnoteValues.Separator);
        if (separator == null)
        {
            separator = CreateFootnoteSeparator(-1, FootnoteEndnoteValues.Separator);
            footnotes.InsertAt(separator, 0);
            log?.Invoke("Added footnote separator (-1).");
        }

        var continuation = footnotes.Elements<Footnote>()
            .FirstOrDefault(f => f.Type?.Value == FootnoteEndnoteValues.ContinuationSeparator);
        if (continuation == null)
        {
            var continuationSeparator = CreateFootnoteSeparator(0, FootnoteEndnoteValues.ContinuationSeparator);
            footnotes.InsertAfter(continuationSeparator, separator);
            log?.Invoke("Added footnote continuation separator (0).");
        }

        footnotes.Save();
    }

    private static void EnsureEndnotesHaveSeparators(EndnotesPart endnotesPart, Action<string>? log)
    {
        var endnotes = endnotesPart.Endnotes ?? new Endnotes();

        if (endnotesPart.Endnotes == null)
            endnotesPart.Endnotes = endnotes;

        var separator = endnotes.Elements<Endnote>()
            .FirstOrDefault(f => f.Type?.Value == FootnoteEndnoteValues.Separator);
        if (separator == null)
        {
            separator = CreateEndnoteSeparator(-1, FootnoteEndnoteValues.Separator);
            endnotes.InsertAt(separator, 0);
            log?.Invoke("Added endnote separator (-1).");
        }

        var continuation = endnotes.Elements<Endnote>()
            .FirstOrDefault(f => f.Type?.Value == FootnoteEndnoteValues.ContinuationSeparator);
        if (continuation == null)
        {
            var continuationSeparator = CreateEndnoteSeparator(0, FootnoteEndnoteValues.ContinuationSeparator);
            endnotes.InsertAfter(continuationSeparator, separator);
            log?.Invoke("Added endnote continuation separator (0).");
        }

        endnotes.Save();
    }

    private static Footnote CreateFootnoteSeparator(int id, FootnoteEndnoteValues type)
    {
        var run = new Run();
        if (type == FootnoteEndnoteValues.Separator)
            run.Append(new SeparatorMark());
        else
            run.Append(new ContinuationSeparatorMark());

        return new Footnote(new Paragraph(run))
        {
            Id = id,
            Type = type
        };
    }

    private static Endnote CreateEndnoteSeparator(int id, FootnoteEndnoteValues type)
    {
        var run = new Run();
        if (type == FootnoteEndnoteValues.Separator)
            run.Append(new SeparatorMark());
        else
            run.Append(new ContinuationSeparatorMark());

        return new Endnote(new Paragraph(run))
        {
            Id = id,
            Type = type
        };
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

            // Fix paths - convert absolute and redundant relative paths
            if (targetAttr != null)
            {
                var target = targetAttr.Value;

                // Fix absolute paths (e.g., /word/footnotes.xml)
                if (target.StartsWith("/"))
                {
                    targetAttr.Value = NormalizeTargetPath(target, baseFolder);
                }
                // Fix redundant relative paths (e.g., ../word/footnotes.xml when in word/)
                else if (target.StartsWith("../") && !string.IsNullOrEmpty(baseFolder))
                {
                    targetAttr.Value = SimplifyRelativePath(target, baseFolder);
                }
            }
        }
    }

    /// <summary>
    /// Simplifies redundant relative paths like "../word/footnotes.xml" to "footnotes.xml"
    /// when the owning part is already in the target folder
    /// </summary>
    private static string SimplifyRelativePath(string relativePath, string baseFolder)
    {
        // baseFolder is like "word/" - the folder containing the owning part
        // relativePath is like "../word/footnotes.xml"

        // If path goes up one level and back into the base folder, simplify it
        // e.g., "../word/footnotes.xml" when baseFolder is "word/" -> "footnotes.xml"
        var baseFolderName = baseFolder.TrimEnd('/');
        var expectedPrefix = "../" + baseFolderName + "/";

        if (relativePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return relativePath[expectedPrefix.Length..];
        }

        // Keep the path as-is if it doesn't match the pattern
        return relativePath;
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
            return relsFolder[..^6] + "/"; // Remove "/_rels" (6 chars), add trailing slash

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
