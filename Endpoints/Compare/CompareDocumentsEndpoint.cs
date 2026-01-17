using FastEndpoints;
using Docxodus;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text;
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
    private const int MaxValidationErrorsToLog = 50;

    // PowerTools namespace that causes Word warnings
    private static readonly XNamespace Pt14 = "http://powertools.codeplex.com/2011";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly HashSet<string> WordRepairDiffFocusEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "word/document.xml",
        "word/settings.xml",
        "word/numbering.xml",
        "docProps/core.xml",
        "docProps/app.xml"
    };

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

            LogValidationResults("Docxodus raw output", result.DocumentByteArray, Logger);
            LogPackageIntegrity("Docxodus raw output", result.DocumentByteArray, Logger);
            TryLogSourceOfTruth(Logger);

            // Clean the output to remove PowerTools internal attributes and fix relationships
            var author = req.Author ?? "Redline API";
            var cleanedBytes = CleanDocument(result.DocumentByteArray, author, message => Logger.LogInformation(message));

            LogValidationResults("Cleaned output", cleanedBytes, Logger);
            LogPackageIntegrity("Cleaned output", cleanedBytes, Logger);
            TryLogWordRepairDiff(cleanedBytes, Logger);

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
    private static byte[] CleanDocument(byte[] docBytes, string author, Action<string>? log = null)
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

                // Word invariants pass - ensure semantic consistency
                FixWordInvariants(doc, author, log);
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

        // Get the XML content and fix self-closing tags
        var xmlContent = encoding.GetString(tempStream.ToArray());
        xmlContent = xmlContent.Replace(" />", "/>");

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

    /// <summary>
    /// Validates a document and logs any errors found
    /// </summary>
    private static void LogValidationResults(string label, byte[] docBytes, ILogger logger)
    {
        try
        {
            using var stream = new MemoryStream(docBytes);
            using var doc = WordprocessingDocument.Open(stream, false);
            var validator = new OpenXmlValidator(FileFormatVersions.Office2021);
            var errors = validator.Validate(doc).ToList();

            logger.LogInformation("{Label}: Found {Count} validation errors", label, errors.Count);
            foreach (var error in errors.Take(MaxValidationErrorsToLog))
            {
                logger.LogWarning("  - {Description} | Part: {Part} | XPath: {XPath}",
                    error.Description, error.Part?.Uri, error.Path?.XPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to validate {Label}", label);
        }
    }

    /// <summary>
    /// Attempts to validate the source of truth file for comparison
    /// </summary>
    private static void TryLogSourceOfTruth(ILogger logger)
    {
        const string sourceOfTruthPath = "/Users/arthrod/temp/Manual Library/temp/redline-endpoint/redline_source_of_truth.docx";
        if (!File.Exists(sourceOfTruthPath))
        {
            logger.LogInformation("Source of truth file not found at {Path}", sourceOfTruthPath);
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(sourceOfTruthPath);
            LogValidationResults("Source of truth", bytes, logger);
            LogPackageIntegrity("Source of truth", bytes, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read source of truth");
        }
    }

    private static void TryLogWordRepairDiff(byte[] cleanedBytes, ILogger logger)
    {
        var repairedPath = FindWordRepairedPath();
        if (repairedPath == null)
        {
            logger.LogInformation("Word-repaired docx not found. Add fixed_redline_by_word.docx to the project root.");
            return;
        }

        try
        {
            var repairedBytes = File.ReadAllBytes(repairedPath);
            logger.LogInformation("Comparing cleaned output to Word-repaired docx: {Path}", repairedPath);
            LogDocxDiff("Cleaned output vs Word repair", cleanedBytes, repairedBytes, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compare cleaned output to Word-repaired docx");
        }
    }

    private static string? FindWordRepairedPath()
    {
        var candidates = new[]
        {
            "fixed_redline_by_word.docx",
            "redline_fixed_by_word.docx",
            "redlined_repaired.docx",
            "redlined_fixed_by_word.docx"
        };

        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
        };

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(root, candidate);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static void LogDocxDiff(string label, byte[] leftBytes, byte[] rightBytes, ILogger logger)
    {
        using var leftStream = new MemoryStream(leftBytes);
        using var rightStream = new MemoryStream(rightBytes);
        using var leftArchive = new ZipArchive(leftStream, ZipArchiveMode.Read, true);
        using var rightArchive = new ZipArchive(rightStream, ZipArchiveMode.Read, true);

        var leftEntries = leftArchive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.FullName))
            .GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var rightEntries = rightArchive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.FullName))
            .GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var missingInLeft = rightEntries.Keys.Except(leftEntries.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var missingInRight = leftEntries.Keys.Except(rightEntries.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var entry in missingInLeft.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning("{Label}: Missing in cleaned output: {Entry}", label, entry);
        }

        foreach (var entry in missingInRight.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning("{Label}: Missing in Word-repaired output: {Entry}", label, entry);
        }

        var differences = 0;
        foreach (var entryName in leftEntries.Keys.Intersect(rightEntries.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var leftEntry = leftEntries[entryName];
            var rightEntry = rightEntries[entryName];

            if (IsXmlEntry(entryName))
            {
                var leftDoc = TryLoadXml(leftEntry, logger, label);
                var rightDoc = TryLoadXml(rightEntry, logger, label);

                if (leftDoc != null && rightDoc != null)
                {
                    var leftNormalized = NormalizeXmlForDiff(leftDoc);
                    var rightNormalized = NormalizeXmlForDiff(rightDoc);

                    if (!XNode.DeepEquals(leftNormalized, rightNormalized))
                    {
                        differences++;
                        logger.LogWarning(
                            "{Label}: XML differs in {Entry} (Cleaned={LeftSize} bytes, Word={RightSize} bytes)",
                            label,
                            entryName,
                            leftEntry.Length,
                            rightEntry.Length);
                        if (ShouldLogXmlDelta(entryName))
                        {
                            LogXmlDeltaSummary(label, entryName, leftDoc, rightDoc, logger);
                        }
                        if (differences >= MaxValidationErrorsToLog)
                            break;
                    }

                    continue;
                }
            }

            var leftBytesEntry = ReadEntryBytes(leftEntry);
            var rightBytesEntry = ReadEntryBytes(rightEntry);
            if (!leftBytesEntry.SequenceEqual(rightBytesEntry))
            {
                differences++;
                logger.LogWarning(
                    "{Label}: Binary differs in {Entry} (Cleaned={LeftSize} bytes, Word={RightSize} bytes)",
                    label,
                    entryName,
                    leftBytesEntry.Length,
                    rightBytesEntry.Length);
                if (differences >= MaxValidationErrorsToLog)
                    break;
            }
        }

        logger.LogInformation(
            "{Label}: Word repair diff summary. MissingInCleaned={MissingInCleaned}, MissingInWord={MissingInWord}, ContentDifferences={ContentDifferences}",
            label,
            missingInLeft.Count,
            missingInRight.Count,
            differences);
    }

    private static bool IsXmlEntry(string entryName)
    {
        return entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
               entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument? TryLoadXml(ZipArchiveEntry entry, ILogger logger, string label)
    {
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Label}: Failed to load XML entry {Entry}", label, entry.FullName);
            return null;
        }
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static XDocument NormalizeXmlForDiff(XDocument document)
    {
        if (document.Root == null)
            return document;

        var normalizedRoot = NormalizeXmlElement(document.Root);
        return new XDocument(document.Declaration, normalizedRoot);
    }

    private static XElement NormalizeXmlElement(XElement element)
    {
        var attributes = element.Attributes()
            .OrderBy(attribute => attribute.Name.NamespaceName)
            .ThenBy(attribute => attribute.Name.LocalName)
            .Select(attribute => new XAttribute(attribute.Name, attribute.Value));

        var nodes = element.Nodes().Select<XNode, XNode>(node => node switch
        {
            XElement child => NormalizeXmlElement(child),
            XCData cdata => new XCData(cdata.Value),
            XText text => new XText(text.Value),
            XComment comment => new XComment(comment.Value),
            XProcessingInstruction pi => new XProcessingInstruction(pi.Target, pi.Data),
            _ => new XText(node.ToString())
        });

        return new XElement(element.Name, attributes, nodes);
    }

    private static bool ShouldLogXmlDelta(string entryName)
    {
        return WordRepairDiffFocusEntries.Contains(entryName);
    }

    private static void LogXmlDeltaSummary(string label, string entryName, XDocument leftDoc, XDocument rightDoc, ILogger logger)
    {
        var leftElementCount = leftDoc.Descendants().Count();
        var rightElementCount = rightDoc.Descendants().Count();
        var leftAttributeCount = leftDoc.Descendants().SelectMany(element => element.Attributes()).Count();
        var rightAttributeCount = rightDoc.Descendants().SelectMany(element => element.Attributes()).Count();

        logger.LogInformation(
            "{Label}: {Entry} counts. Elements: Cleaned={LeftElements}, Word={RightElements}. Attributes: Cleaned={LeftAttributes}, Word={RightAttributes}",
            label,
            entryName,
            leftElementCount,
            rightElementCount,
            leftAttributeCount,
            rightAttributeCount);

        LogCountDeltas(label, entryName, "Element", BuildNameCounts(leftDoc.Descendants().Select(element => element.Name.ToString())), BuildNameCounts(rightDoc.Descendants().Select(element => element.Name.ToString())), logger);
        LogCountDeltas(label, entryName, "Attribute", BuildNameCounts(leftDoc.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Name.ToString())), BuildNameCounts(rightDoc.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Name.ToString())), logger);
    }

    private static Dictionary<string, int> BuildNameCounts(IEnumerable<string> names)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (counts.TryGetValue(name, out var existing))
                counts[name] = existing + 1;
            else
                counts[name] = 1;
        }

        return counts;
    }

    private static void LogCountDeltas(
        string label,
        string entryName,
        string kind,
        Dictionary<string, int> leftCounts,
        Dictionary<string, int> rightCounts,
        ILogger logger)
    {
        var differences = leftCounts.Keys
            .Union(rightCounts.Keys)
            .Select(name =>
            {
                leftCounts.TryGetValue(name, out var left);
                rightCounts.TryGetValue(name, out var right);
                return new
                {
                    Name = name,
                    Left = left,
                    Right = right,
                    Delta = right - left
                };
            })
            .Where(item => item.Delta != 0)
            .OrderByDescending(item => Math.Abs(item.Delta))
            .Take(MaxValidationErrorsToLog)
            .ToList();

        foreach (var item in differences)
        {
            logger.LogWarning(
                "{Label}: {Entry} {Kind} delta {Name} (Cleaned={Left}, Word={Right}, Delta={Delta})",
                label,
                entryName,
                kind,
                item.Name,
                item.Left,
                item.Right,
                item.Delta);
        }
    }

    private static void LogPackageIntegrity(string label, byte[] docBytes, ILogger logger)
    {
        try
        {
            using var stream = new MemoryStream(docBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);

            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.FullName))
                .ToList();
            var entryNames = entries.Select(entry => entry.FullName).ToList();
            var entrySet = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);

            var duplicateIssues = LogDuplicateEntries(label, entryNames, logger);
            var contentTypeIssues = LogContentTypeIssues(label, entries, entrySet, logger);
            var relIssues = 0;
            var relCount = 0;

            foreach (var relEntry in entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                relCount++;
                using var relStream = relEntry.Open();
                var relsDoc = XDocument.Load(relStream);
                relIssues += LogRelationshipIssues(label, relsDoc, relEntry.FullName, entrySet, logger);
            }

            var relReferenceIssues = LogRelationshipReferenceIssues(label, entries, logger);
            var noteReferenceIssues = LogNoteReferenceIssues(label, entries, logger);

            logger.LogInformation(
                "{Label}: Package integrity summary. Entries={EntryCount}, Relationships={RelCount}, DuplicateIssues={DuplicateIssues}, ContentTypeIssues={ContentTypeIssues}, RelationshipIssues={RelIssues}, RelationshipReferenceIssues={RelReferenceIssues}, NoteReferenceIssues={NoteReferenceIssues}",
                label,
                entries.Count,
                relCount,
                duplicateIssues,
                contentTypeIssues,
                relIssues,
                relReferenceIssues,
                noteReferenceIssues);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed package integrity check for {Label}", label);
        }
    }

    private static int LogDuplicateEntries(string label, List<string> entryNames, ILogger logger)
    {
        var issues = 0;
        var exactDuplicates = entryNames
            .GroupBy(name => name)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in exactDuplicates.Take(MaxValidationErrorsToLog))
        {
            issues++;
            logger.LogWarning("{Label}: Duplicate zip entry {Entry} (x{Count})", label, group.Key, group.Count());
        }

        var caseInsensitiveDuplicates = entryNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Where(group => group.Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();

        foreach (var group in caseInsensitiveDuplicates.Take(MaxValidationErrorsToLog))
        {
            var variants = string.Join(", ", group.Distinct(StringComparer.Ordinal));
            logger.LogWarning(
                "{Label}: Case-colliding zip entries ({Count}) for {Entry}: {Variants}",
                label,
                group.Count(),
                group.Key,
                variants);
            issues++;
        }

        return issues;
    }

    private static int LogRelationshipReferenceIssues(string label, List<ZipArchiveEntry> entries, ILogger logger)
    {
        var issues = 0;
        var relsIdMap = BuildRelationshipIdMap(entries);
        var relAttributeNames = new HashSet<string>(new[] { "id", "embed", "link" }, StringComparer.OrdinalIgnoreCase);

        foreach (var contentEntry in entries.Where(entry => IsContentPartWithRelationships(entry.FullName)))
        {
            using var contentStream = contentEntry.Open();
            var contentDoc = XDocument.Load(contentStream);

            var relsPath = GetRelsPathForContentPart(contentEntry.FullName);
            relsIdMap.TryGetValue(relsPath, out var relIds);

            var relAttrs = contentDoc.Descendants()
                .SelectMany(element => element.Attributes())
                .Where(attr => attr.Name.Namespace == R && relAttributeNames.Contains(attr.Name.LocalName))
                .ToList();

            if (relAttrs.Count == 0)
                continue;

            if (relIds == null)
            {
                issues++;
                logger.LogWarning(
                    "{Label}: Missing .rels file for {Part} but found relationship references (expected {RelsPath})",
                    label,
                    contentEntry.FullName,
                    relsPath);
                if (issues >= MaxValidationErrorsToLog)
                    break;
                continue;
            }

            foreach (var attr in relAttrs)
            {
                if (!relIds.Contains(attr.Value))
                {
                    issues++;
                    logger.LogWarning(
                        "{Label}: Missing relationship Id {Id} referenced in {Part} (rels: {RelsPath})",
                        label,
                        attr.Value,
                        contentEntry.FullName,
                        relsPath);
                    if (issues >= MaxValidationErrorsToLog)
                        return issues;
                }
            }
        }

        return issues;
    }

    private static Dictionary<string, HashSet<string>> BuildRelationshipIdMap(List<ZipArchiveEntry> entries)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var relEntry in entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            using var relStream = relEntry.Open();
            var relsDoc = XDocument.Load(relStream);
            var ids = relsDoc.Root?
                .Elements(Rel + "Relationship")
                .Select(rel => rel.Attribute("Id")?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            map[relEntry.FullName] = ids;
        }

        return map;
    }

    private static int LogNoteReferenceIssues(string label, List<ZipArchiveEntry> entries, ILogger logger)
    {
        var issues = 0;
        var footnoteIds = LoadNoteIds(entries, "word/footnotes.xml", W + "footnote");
        var endnoteIds = LoadNoteIds(entries, "word/endnotes.xml", W + "endnote");
        var commentIds = LoadNoteIds(entries, "word/comments.xml", W + "comment");

        foreach (var contentEntry in entries.Where(entry => IsContentPartWithRelationships(entry.FullName)))
        {
            using var contentStream = contentEntry.Open();
            var contentDoc = XDocument.Load(contentStream);

            issues += LogMissingNoteReferences(label, contentEntry.FullName, contentDoc, W + "footnoteReference", footnoteIds, logger);
            if (issues >= MaxValidationErrorsToLog)
                return issues;

            issues += LogMissingNoteReferences(label, contentEntry.FullName, contentDoc, W + "endnoteReference", endnoteIds, logger);
            if (issues >= MaxValidationErrorsToLog)
                return issues;

            issues += LogMissingNoteReferences(label, contentEntry.FullName, contentDoc, W + "commentReference", commentIds, logger);
            if (issues >= MaxValidationErrorsToLog)
                return issues;

            issues += LogMissingNoteReferences(label, contentEntry.FullName, contentDoc, W + "commentRangeStart", commentIds, logger);
            if (issues >= MaxValidationErrorsToLog)
                return issues;

            issues += LogMissingNoteReferences(label, contentEntry.FullName, contentDoc, W + "commentRangeEnd", commentIds, logger);
            if (issues >= MaxValidationErrorsToLog)
                return issues;
        }

        return issues;
    }

    private static HashSet<string> LoadNoteIds(List<ZipArchiveEntry> entries, string partName, XName elementName)
    {
        var entry = entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, partName, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        return doc.Descendants(elementName)
            .Select(element => element.Attribute(W + "id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int LogMissingNoteReferences(
        string label,
        string partName,
        XDocument contentDoc,
        XName referenceElement,
        HashSet<string> knownIds,
        ILogger logger)
    {
        var issues = 0;
        var references = contentDoc.Descendants(referenceElement)
            .Select(element => element.Attribute(W + "id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id));

        foreach (var id in references)
        {
            if (!knownIds.Contains(id!))
            {
                issues++;
                logger.LogWarning(
                    "{Label}: Missing {ReferenceType} id {Id} referenced in {Part}",
                    label,
                    referenceElement.LocalName,
                    id,
                    partName);
                if (issues >= MaxValidationErrorsToLog)
                    return issues;
            }
        }

        return issues;
    }

    private static int LogContentTypeIssues(
        string label,
        List<ZipArchiveEntry> entries,
        HashSet<string> entrySet,
        ILogger logger)
    {
        var issues = 0;
        var contentTypesEntry = entries.FirstOrDefault(entry => entry.FullName == "[Content_Types].xml");
        if (contentTypesEntry == null)
        {
            logger.LogWarning("{Label}: Missing [Content_Types].xml", label);
            return ++issues;
        }

        using var contentTypesStream = contentTypesEntry.Open();
        var contentTypesDoc = XDocument.Load(contentTypesStream);
        var root = contentTypesDoc.Root;
        if (root == null)
        {
            logger.LogWarning("{Label}: [Content_Types].xml has no root", label);
            return ++issues;
        }

        var ns = root.Name.Namespace;
        var overrides = root.Elements(ns + "Override")
            .Select(element => element.Attribute("PartName")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();

        var defaults = root.Elements(ns + "Default")
            .Select(element => element.Attribute("Extension")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();

        var duplicateOverrides = overrides
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in duplicateOverrides.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning("{Label}: Duplicate content type override for {Part} (x{Count})", label, group.Key, group.Count());
            issues++;
        }

        var duplicateDefaults = defaults
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in duplicateDefaults.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning("{Label}: Duplicate content type default for extension {Extension} (x{Count})", label, group.Key, group.Count());
            issues++;
        }

        var overrideParts = overrides
            .Select(value => value.TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultExtensions = defaults
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingOverrideParts = overrides
            .Select(value => value.TrimStart('/'))
            .Where(part => !entrySet.Contains(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var part in missingOverrideParts.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning("{Label}: Content type override references missing part {Part}", label, part);
            issues++;
        }

        var entriesMissingContentTypes = entries
            .Select(entry => entry.FullName)
            .Where(name => name != "[Content_Types].xml")
            .Where(name => !overrideParts.Contains(name))
            .Where(name =>
            {
                var extension = Path.GetExtension(name).TrimStart('.');
                return string.IsNullOrEmpty(extension) || !defaultExtensions.Contains(extension);
            })
            .ToList();

        foreach (var part in entriesMissingContentTypes.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning("{Label}: Missing content type for part {Part}", label, part);
            issues++;
        }

        return issues;
    }

    private static int LogRelationshipIssues(
        string label,
        XDocument relsDoc,
        string relsPath,
        HashSet<string> entrySet,
        ILogger logger)
    {
        var issues = 0;
        if (relsDoc.Root == null)
        {
            logger.LogWarning("{Label}: Relationships file {RelsPath} has no root", label, relsPath);
            return ++issues;
        }

        var baseFolder = GetBaseFolderForRelsFile(relsPath);
        var relationships = relsDoc.Root.Elements(Rel + "Relationship").ToList();

        var duplicateIds = relationships
            .Select(rel => rel.Attribute("Id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in duplicateIds.Take(MaxValidationErrorsToLog))
        {
            logger.LogWarning(
                "{Label}: Duplicate relationship Id {Id} (x{Count}) in {RelsPath}",
                label,
                group.Key,
                group.Count(),
                relsPath);
            issues++;
        }

        var issueCount = 0;
        foreach (var rel in relationships)
        {
            var targetAttr = rel.Attribute("Target");
            if (targetAttr == null || string.IsNullOrWhiteSpace(targetAttr.Value))
            {
                logger.LogWarning("{Label}: Empty relationship target in {RelsPath}", label, relsPath);
                issues++;
                if (++issueCount >= MaxValidationErrorsToLog)
                    break;
                continue;
            }

            var targetMode = rel.Attribute("TargetMode")?.Value;
            if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
                continue;

            var resolvedTarget = ResolveTargetPath(targetAttr.Value, baseFolder, out var escapedRoot);
            if (escapedRoot)
            {
                logger.LogWarning(
                    "{Label}: Relationship target escapes package root in {RelsPath}: {Target}",
                    label,
                    relsPath,
                    targetAttr.Value);
                issues++;
                if (++issueCount >= MaxValidationErrorsToLog)
                    break;
            }

            if (string.IsNullOrWhiteSpace(resolvedTarget) || !entrySet.Contains(resolvedTarget))
            {
                logger.LogWarning(
                    "{Label}: Missing relationship target in {RelsPath}: {Target} -> {Resolved}",
                    label,
                    relsPath,
                    targetAttr.Value,
                    resolvedTarget);
                issues++;
                if (++issueCount >= MaxValidationErrorsToLog)
                    break;
            }
        }

        return issues;
    }

    private static string ResolveTargetPath(string target, string baseFolder, out bool escapedRoot)
    {
        escapedRoot = false;
        var normalizedTarget = target.Replace('\\', '/');

        string combined;
        if (normalizedTarget.StartsWith("/"))
            combined = normalizedTarget.TrimStart('/');
        else if (string.IsNullOrEmpty(baseFolder))
            combined = normalizedTarget;
        else
            combined = baseFolder + normalizedTarget;

        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count == 0)
                    escapedRoot = true;
                else
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    /// <summary>
    /// Fixes Word invariants that aren't covered by OpenXmlValidator but cause "unreadable content" warnings
    /// </summary>
    private static void FixWordInvariants(WordprocessingDocument doc, string author, Action<string>? log)
    {
        ConvertMoveOperationsToDelIns(doc, log);  // Convert fragile moves to simpler del/ins
        EnsureParagraphIds(doc, log);
        NormalizeTrackedChangeDates(doc, log);
        EnsureTableCellsHaveAtLeastOneParagraph(doc, log);
        EnsureCommentPartsExistIfReferenced(doc, author, log);
        EnsureUniqueRevisionIds(doc, log);
        EnsureSectionPropertiesHaveRsid(doc, log);
    }

    /// <summary>
    /// Ensures all revision w:id attributes are unique across the document.
    /// According to ECMA-376, w:id on revision elements must be unique within document.xml.
    /// Docxodus sometimes generates duplicate IDs (e.g., w:moveFrom and w:del both with id="21").
    /// </summary>
    private static void EnsureUniqueRevisionIds(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        var totalReassigned = 0;

        foreach (var root in EnumerateStoryRoots(main))
        {
            // Collect all revision elements with w:id attributes
            var revisionElements = new List<OpenXmlElement>();

            revisionElements.AddRange(root.Descendants<DeletedRun>());
            revisionElements.AddRange(root.Descendants<InsertedRun>());
            revisionElements.AddRange(root.Descendants<Deleted>());
            revisionElements.AddRange(root.Descendants<Inserted>());
            revisionElements.AddRange(root.Descendants<MoveFromRun>());
            revisionElements.AddRange(root.Descendants<MoveToRun>());
            revisionElements.AddRange(root.Descendants<MoveFromRangeStart>());
            revisionElements.AddRange(root.Descendants<MoveFromRangeEnd>());
            revisionElements.AddRange(root.Descendants<MoveToRangeStart>());
            revisionElements.AddRange(root.Descendants<MoveToRangeEnd>());

            // Track used IDs and find duplicates
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            var elementsToReassign = new List<OpenXmlElement>();
            var maxId = 0;

            foreach (var element in revisionElements)
            {
                var idValue = GetRevisionId(element);
                if (idValue == null) continue;

                if (int.TryParse(idValue, out var numericId))
                {
                    maxId = Math.Max(maxId, numericId);
                }

                if (usedIds.Contains(idValue))
                {
                    // Duplicate found - mark for reassignment
                    elementsToReassign.Add(element);
                }
                else
                {
                    usedIds.Add(idValue);
                }
            }

            // Reassign duplicate IDs
            var nextId = maxId + 1;
            foreach (var element in elementsToReassign)
            {
                var newId = nextId.ToString();
                SetRevisionId(element, newId);
                usedIds.Add(newId);
                nextId++;
                totalReassigned++;
            }

            if (elementsToReassign.Count > 0)
                root.Save();
        }

        if (totalReassigned > 0)
            log?.Invoke($"Reassigned {totalReassigned} duplicate revision IDs to ensure uniqueness.");
    }

    /// <summary>
    /// Gets the w:id attribute value from a revision element
    /// </summary>
    private static string? GetRevisionId(OpenXmlElement element)
    {
        return element switch
        {
            DeletedRun del => del.Id?.Value,
            InsertedRun ins => ins.Id?.Value,
            Deleted d => d.Id?.Value,
            Inserted i => i.Id?.Value,
            MoveFromRun mf => mf.Id?.Value,
            MoveToRun mt => mt.Id?.Value,
            MoveFromRangeStart mfrs => mfrs.Id?.Value,
            MoveFromRangeEnd mfre => mfre.Id?.Value,
            MoveToRangeStart mtrs => mtrs.Id?.Value,
            MoveToRangeEnd mtre => mtre.Id?.Value,
            _ => null
        };
    }

    /// <summary>
    /// Sets the w:id attribute value on a revision element
    /// </summary>
    private static void SetRevisionId(OpenXmlElement element, string newId)
    {
        switch (element)
        {
            case DeletedRun del:
                del.Id = newId;
                break;
            case InsertedRun ins:
                ins.Id = newId;
                break;
            case Deleted d:
                d.Id = newId;
                break;
            case Inserted i:
                i.Id = newId;
                break;
            case MoveFromRun mf:
                mf.Id = newId;
                break;
            case MoveToRun mt:
                mt.Id = newId;
                break;
            case MoveFromRangeStart mfrs:
                mfrs.Id = newId;
                break;
            case MoveFromRangeEnd mfre:
                mfre.Id = newId;
                break;
            case MoveToRangeStart mtrs:
                mtrs.Id = newId;
                break;
            case MoveToRangeEnd mtre:
                mtre.Id = newId;
                break;
        }
    }

    /// <summary>
    /// Ensures all w:sectPr elements have a w:rsidR attribute.
    /// Docxodus strips this required attribute, causing Word "unreadable content" warnings.
    /// Generates a new unique rsid that doesn't conflict with existing ones.
    /// </summary>
    private static void EnsureSectionPropertiesHaveRsid(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main?.Document?.Body == null) return;

        var sectionsFixed = 0;

        // Collect all existing rsid values to avoid conflicts
        var existingRsids = GetAllExistingRsids(doc);

        // Generate a new unique rsid for sectPr elements
        string rsidValue = GenerateUniqueRsid(existingRsids);

        // Find all SectionProperties in the document body
        foreach (var sectPr in main.Document.Body.Descendants<SectionProperties>())
        {
            if (sectPr.RsidR == null)
            {
                sectPr.RsidR = new HexBinaryValue(rsidValue);
                sectionsFixed++;
            }
        }

        if (sectionsFixed > 0)
        {
            // Also add this new rsid to settings.xml rsids list for consistency
            AddRsidToSettings(doc, rsidValue);
            main.Document.Save();
            log?.Invoke($"Added w:rsidR=\"{rsidValue}\" to {sectionsFixed} section properties.");
        }
    }

    /// <summary>
    /// Collects all existing rsid values from settings.xml
    /// </summary>
    private static HashSet<string> GetAllExistingRsids(WordprocessingDocument doc)
    {
        var rsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var settingsPart = doc.MainDocumentPart?.DocumentSettingsPart;
        if (settingsPart?.Settings == null) return rsids;

        // Get rsidRoot
        var rsidRoot = settingsPart.Settings.Descendants<RsidRoot>().FirstOrDefault();
        if (rsidRoot?.Val?.Value != null)
            rsids.Add(rsidRoot.Val.Value);

        // Get all rsid entries
        foreach (var rsid in settingsPart.Settings.Descendants<Rsid>())
        {
            if (rsid.Val?.Value != null)
                rsids.Add(rsid.Val.Value);
        }

        return rsids;
    }

    /// <summary>
    /// Generates a unique rsid value that doesn't exist in the given set.
    /// Per ECMA-376, rsid is ST_LongHexNumber (4 bytes / 8 hex chars).
    /// Word generates these randomly, typically in range 00xxxxxx.
    /// </summary>
    private static string GenerateUniqueRsid(HashSet<string> existingRsids)
    {
        var random = new Random();
        string candidate;
        do
        {
            // Generate in Word's typical range: 00010000 to 00FFFFFF
            var value = random.Next(0x00010000, 0x00FFFFFF);
            candidate = value.ToString("X8");
        } while (existingRsids.Contains(candidate));

        return candidate;
    }

    /// <summary>
    /// Adds a new rsid value to the settings.xml rsids list
    /// </summary>
    private static void AddRsidToSettings(WordprocessingDocument doc, string rsidValue)
    {
        var settingsPart = doc.MainDocumentPart?.DocumentSettingsPart;
        if (settingsPart?.Settings == null) return;

        // Find the rsids container
        var rsidsElement = settingsPart.Settings.Descendants<Rsids>().FirstOrDefault();
        if (rsidsElement == null) return;

        // Add new rsid entry
        rsidsElement.AppendChild(new Rsid { Val = new HexBinaryValue(rsidValue) });
        settingsPart.Settings.Save();
    }

    /// <summary>
    /// Converts move operations (moveFrom/moveTo) to simpler del/ins operations.
    /// Move operations in OOXML are fragile and Word is very strict about them.
    /// The simpler del/ins approach is more robust.
    /// - moveFrom → del (content moved from here = deleted)
    /// - moveTo → ins (content moved to here = inserted)
    /// - Remove all moveFromRangeStart/End, moveToRangeStart/End elements
    /// </summary>
    private static void ConvertMoveOperationsToDelIns(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        var totalConverted = 0;
        var totalRangesRemoved = 0;

        foreach (var root in EnumerateStoryRoots(main))
        {
            // Convert MoveFromRun to DeletedRun
            foreach (var moveFrom in root.Descendants<MoveFromRun>().ToList())
            {
                var del = new DeletedRun
                {
                    Author = moveFrom.Author?.Value,
                    Date = moveFrom.Date?.Value,
                    Id = moveFrom.Id?.Value
                };

                // Move all children to the new del element
                foreach (var child in moveFrom.ChildElements.ToList())
                {
                    child.Remove();
                    del.AppendChild(child);
                }

                moveFrom.InsertAfterSelf(del);
                moveFrom.Remove();
                totalConverted++;
            }

            // Convert MoveToRun to InsertedRun
            foreach (var moveTo in root.Descendants<MoveToRun>().ToList())
            {
                var ins = new InsertedRun
                {
                    Author = moveTo.Author?.Value,
                    Date = moveTo.Date?.Value,
                    Id = moveTo.Id?.Value
                };

                // Move all children to the new ins element
                foreach (var child in moveTo.ChildElements.ToList())
                {
                    child.Remove();
                    ins.AppendChild(child);
                }

                moveTo.InsertAfterSelf(ins);
                moveTo.Remove();
                totalConverted++;
            }

            // Remove all move range markers (they're no longer needed)
            foreach (var rangeStart in root.Descendants<MoveFromRangeStart>().ToList())
            {
                rangeStart.Remove();
                totalRangesRemoved++;
            }
            foreach (var rangeEnd in root.Descendants<MoveFromRangeEnd>().ToList())
            {
                rangeEnd.Remove();
                totalRangesRemoved++;
            }
            foreach (var rangeStart in root.Descendants<MoveToRangeStart>().ToList())
            {
                rangeStart.Remove();
                totalRangesRemoved++;
            }
            foreach (var rangeEnd in root.Descendants<MoveToRangeEnd>().ToList())
            {
                rangeEnd.Remove();
                totalRangesRemoved++;
            }

            if (totalConverted > 0 || totalRangesRemoved > 0)
                root.Save();
        }

        if (totalConverted > 0 || totalRangesRemoved > 0)
            log?.Invoke($"Converted {totalConverted} move operations to del/ins, removed {totalRangesRemoved} range markers.");
    }

    /// <summary>
    /// Deduplicates move operations by w:name attribute.
    /// Docxodus creates multiple moveFrom/moveTo with the same name, but Word expects only one pair per name.
    /// Also keeps only one MoveFromRun and one MoveToRun per unique move.
    /// NOTE: This is now obsolete since ConvertMoveOperationsToDelIns converts all moves.
    /// </summary>
    private static void DeduplicateMoveOperations(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        var totalRemoved = 0;

        foreach (var root in EnumerateStoryRoots(main))
        {
            // Track which move names we've seen for RangeStart elements
            var seenMoveFromNames = new HashSet<string>();
            var seenMoveToNames = new HashSet<string>();
            // Track valid RangeStart IDs (ones we're keeping)
            var validMoveFromRangeIds = new HashSet<string>();
            var validMoveToRangeIds = new HashSet<string>();

            // Process MoveFromRangeStart - keep first per name, remove duplicates
            var moveFromStarts = root.Descendants<MoveFromRangeStart>().ToList();
            foreach (var el in moveFromStarts)
            {
                var name = el.Name?.Value;
                var id = el.Id?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                if (seenMoveFromNames.Contains(name))
                {
                    // Remove duplicate RangeStart and its RangeEnd
                    if (id != null)
                    {
                        var rangeEnd = root.Descendants<MoveFromRangeEnd>()
                            .FirstOrDefault(e => e.Id?.Value == id);
                        rangeEnd?.Remove();
                        totalRemoved++;
                    }
                    el.Remove();
                    totalRemoved++;
                }
                else
                {
                    seenMoveFromNames.Add(name);
                    if (id != null) validMoveFromRangeIds.Add(id);
                }
            }

            // Process MoveToRangeStart - keep first per name, remove duplicates
            var moveToStarts = root.Descendants<MoveToRangeStart>().ToList();
            foreach (var el in moveToStarts)
            {
                var name = el.Name?.Value;
                var id = el.Id?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                if (seenMoveToNames.Contains(name))
                {
                    // Remove duplicate RangeStart and its RangeEnd
                    if (id != null)
                    {
                        var rangeEnd = root.Descendants<MoveToRangeEnd>()
                            .FirstOrDefault(e => e.Id?.Value == id);
                        rangeEnd?.Remove();
                        totalRemoved++;
                    }
                    el.Remove();
                    totalRemoved++;
                }
                else
                {
                    seenMoveToNames.Add(name);
                    if (id != null) validMoveToRangeIds.Add(id);
                }
            }

            // Now process MoveFromRun - keep only as many as we have unique move names
            var moveFromRuns = root.Descendants<MoveFromRun>().ToList();
            var moveFromKept = 0;
            var maxMoveFrom = seenMoveFromNames.Count; // Keep one per unique name
            foreach (var mf in moveFromRuns)
            {
                if (moveFromKept < maxMoveFrom)
                {
                    moveFromKept++;
                    continue; // Keep this one
                }
                // Remove excess MoveFromRun elements
                mf.Remove();
                totalRemoved++;
            }

            // Now process MoveToRun - keep only as many as we have unique move names
            var moveToRuns = root.Descendants<MoveToRun>().ToList();
            var moveToKept = 0;
            var maxMoveTo = seenMoveToNames.Count; // Keep one per unique name
            foreach (var mt in moveToRuns)
            {
                if (moveToKept < maxMoveTo)
                {
                    moveToKept++;
                    continue; // Keep this one
                }
                // Remove excess MoveToRun elements
                mt.Remove();
                totalRemoved++;
            }

            if (totalRemoved > 0)
                root.Save();
        }

        if (totalRemoved > 0)
            log?.Invoke($"Removed {totalRemoved} duplicate move operations.");
    }

    /// <summary>
    /// Normalizes tracked change dates to UTC format without microseconds.
    /// Word requires dates in format: 2026-01-16T19:34:34Z (not timezone offset or microseconds)
    /// </summary>
    private static void NormalizeTrackedChangeDates(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        var totalFixed = 0;

        foreach (var root in EnumerateStoryRoots(main))
        {
            var fixedCount = 0;

            // Fix dates on all tracked change elements
            foreach (var del in root.Descendants<DeletedRun>())
            {
                if (del.Date?.HasValue == true)
                {
                    del.Date = NormalizeDateToUtc(del.Date.Value);
                    fixedCount++;
                }
            }

            foreach (var ins in root.Descendants<InsertedRun>())
            {
                if (ins.Date?.HasValue == true)
                {
                    ins.Date = NormalizeDateToUtc(ins.Date.Value);
                    fixedCount++;
                }
            }

            foreach (var del in root.Descendants<Deleted>())
            {
                if (del.Date?.HasValue == true)
                {
                    del.Date = NormalizeDateToUtc(del.Date.Value);
                    fixedCount++;
                }
            }

            // Handle Inserted (w:ins inside rPr for formatting changes)
            foreach (var ins in root.Descendants<Inserted>())
            {
                if (ins.Date?.HasValue == true)
                {
                    ins.Date = NormalizeDateToUtc(ins.Date.Value);
                    fixedCount++;
                }
            }

            foreach (var moveFrom in root.Descendants<MoveFromRun>())
            {
                if (moveFrom.Date?.HasValue == true)
                {
                    moveFrom.Date = NormalizeDateToUtc(moveFrom.Date.Value);
                    fixedCount++;
                }
            }

            foreach (var moveTo in root.Descendants<MoveToRun>())
            {
                if (moveTo.Date?.HasValue == true)
                {
                    moveTo.Date = NormalizeDateToUtc(moveTo.Date.Value);
                    fixedCount++;
                }
            }

            // Also fix MoveFromRangeStart and MoveToRangeStart
            foreach (var moveFromStart in root.Descendants<MoveFromRangeStart>())
            {
                if (moveFromStart.Date?.HasValue == true)
                {
                    moveFromStart.Date = NormalizeDateToUtc(moveFromStart.Date.Value);
                    fixedCount++;
                }
            }

            foreach (var moveToStart in root.Descendants<MoveToRangeStart>())
            {
                if (moveToStart.Date?.HasValue == true)
                {
                    moveToStart.Date = NormalizeDateToUtc(moveToStart.Date.Value);
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
                root.Save();

            totalFixed += fixedCount;
        }

        if (totalFixed > 0)
            log?.Invoke($"Normalized {totalFixed} tracked change dates to UTC format.");
    }

    /// <summary>
    /// Converts a DateTime to UTC and truncates to seconds (removes microseconds)
    /// </summary>
    private static DateTime NormalizeDateToUtc(DateTime date)
    {
        var utc = date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
    }

    /// <summary>
    /// Ensures all paragraphs have w14:paraId and w14:textId attributes.
    /// Word requires these for proper tracked changes handling.
    /// </summary>
    private static void EnsureParagraphIds(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        // Generate a consistent rsidR value for this document
        var rsidR = GenerateRsid();
        var totalAdded = 0;

        foreach (var root in EnumerateStoryRoots(main))
        {
            var added = 0;
            foreach (var para in root.Descendants<Paragraph>())
            {
                // Check if paraId already exists
                var existingParaId = para.GetAttributes()
                    .FirstOrDefault(a => a.LocalName == "paraId" &&
                        a.NamespaceUri == "http://schemas.microsoft.com/office/word/2010/wordml");

                if (existingParaId.Value == null)
                {
                    // Add w14:paraId
                    para.SetAttribute(new OpenXmlAttribute(
                        "w14", "paraId",
                        "http://schemas.microsoft.com/office/word/2010/wordml",
                        GenerateParaId()));

                    // Add w14:textId
                    para.SetAttribute(new OpenXmlAttribute(
                        "w14", "textId",
                        "http://schemas.microsoft.com/office/word/2010/wordml",
                        "77777777"));

                    // Add w:rsidR if not present
                    if (para.RsidParagraphAddition == null)
                        para.RsidParagraphAddition = rsidR;

                    // Add w:rsidRDefault if not present
                    if (para.RsidRunAdditionDefault == null)
                        para.RsidRunAdditionDefault = "00000000";

                    added++;
                }
            }

            if (added > 0)
                root.Save();

            totalAdded += added;
        }

        if (totalAdded > 0)
            log?.Invoke($"Added paragraph IDs (w14:paraId/textId) to {totalAdded} paragraphs.");
    }

    private static readonly Random _random = new();

    /// <summary>
    /// Generates a unique 8-character hex paragraph ID.
    /// Value must be less than 0x80000000 per OOXML spec.
    /// </summary>
    private static string GenerateParaId()
    {
        int value;
        lock (_random) { value = _random.Next(0x00000001, 0x7FFFFFFF); }
        return value.ToString("X8");
    }

    /// <summary>
    /// Generates an 8-character hex revision save ID.
    /// Word's rsid values follow convention of starting with "00" (range 0x00000001 to 0x00FFFFFF).
    /// </summary>
    private static string GenerateRsid()
    {
        int value;
        lock (_random) { value = _random.Next(0x00000001, 0x00FFFFFF); }
        return value.ToString("X8");
    }

    /// <summary>
    /// Enumerates all story root elements (main document, headers, footers, footnotes, endnotes)
    /// </summary>
    private static IEnumerable<OpenXmlPartRootElement> EnumerateStoryRoots(MainDocumentPart main)
    {
        if (main.Document != null)
            yield return main.Document;

        foreach (var h in main.HeaderParts)
            if (h.Header != null)
                yield return h.Header;

        foreach (var f in main.FooterParts)
            if (f.Footer != null)
                yield return f.Footer;

        if (main.FootnotesPart?.Footnotes != null)
            yield return main.FootnotesPart.Footnotes;

        if (main.EndnotesPart?.Endnotes != null)
            yield return main.EndnotesPart.Endnotes;
    }

    /// <summary>
    /// Ensures every table cell has at least one block child (paragraph).
    /// Word requires this but OpenXmlValidator may not catch missing block content.
    /// </summary>
    private static void EnsureTableCellsHaveAtLeastOneParagraph(WordprocessingDocument doc, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        foreach (var root in EnumerateStoryRoots(main))
        {
            var emptyCells = 0;

            foreach (var tc in root.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
            {
                // If tc contains only tcPr (or nothing), Word often repairs
                var hasNonPropertiesChild = tc.ChildElements.Any(e => e is not TableCellProperties);

                if (!hasNonPropertiesChild)
                {
                    tc.AppendChild(new Paragraph());
                    emptyCells++;
                }
            }

            if (emptyCells > 0)
            {
                log?.Invoke($"Added <w:p/> to {emptyCells} empty table cells in {root.GetType().Name}.");
                root.Save();
            }
        }
    }

    /// <summary>
    /// Ensures comment parts exist when comment markers are referenced in story parts.
    /// This matches what Word does during repair by creating the four comment parts.
    /// </summary>
    private static void EnsureCommentPartsExistIfReferenced(WordprocessingDocument doc, string author, Action<string>? log)
    {
        var main = doc.MainDocumentPart;
        if (main == null) return;

        // Collect all comment IDs referenced anywhere in the story parts
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in EnumerateStoryRoots(main))
        {
            foreach (var s in root.Descendants<CommentRangeStart>())
                if (!string.IsNullOrWhiteSpace(s.Id?.Value)) referencedIds.Add(s.Id!.Value);

            foreach (var e in root.Descendants<CommentRangeEnd>())
                if (!string.IsNullOrWhiteSpace(e.Id?.Value)) referencedIds.Add(e.Id!.Value);

            foreach (var r in root.Descendants<CommentReference>())
                if (!string.IsNullOrWhiteSpace(r.Id?.Value)) referencedIds.Add(r.Id!.Value);
        }

        if (referencedIds.Count == 0)
            return;

        log?.Invoke($"Found {referencedIds.Count} referenced comment ids. Ensuring comments parts exist...");

        // 1) comments.xml
        var commentsPart =
            main.GetPartsOfType<WordprocessingCommentsPart>().FirstOrDefault()
            ?? main.AddNewPart<WordprocessingCommentsPart>();

        commentsPart.Comments ??= new Comments();

        var initials = BuildInitials(author);

        var existing = commentsPart.Comments
            .Elements<DocumentFormat.OpenXml.Wordprocessing.Comment>()
            .Select(c => c.Id?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var id in referencedIds)
        {
            if (existing.Contains(id))
                continue;

            // Minimal placeholder comment so Word doesn't repair by creating parts
            var c = new DocumentFormat.OpenXml.Wordprocessing.Comment
            {
                Id = id,
                Author = author,
                Initials = initials,
                Date = DateTime.UtcNow
            };

            c.AppendChild(new Paragraph(new Run(new Text(""))));
            commentsPart.Comments.AppendChild(c);
            added++;
        }

        if (added > 0)
            log?.Invoke($"Added {added} placeholder <w:comment> elements to comments.xml.");

        commentsPart.Comments.Save();

        // 2) commentsExtended.xml (w15:commentsEx) - additional comment info
        var commentsExPart =
            main.GetPartsOfType<WordprocessingCommentsExPart>().FirstOrDefault()
            ?? main.AddNewPart<WordprocessingCommentsExPart>();

        commentsExPart.CommentsEx ??= new DocumentFormat.OpenXml.Office2013.Word.CommentsEx();
        commentsExPart.CommentsEx.Save();

        // 3) commentsIds.xml (w16cid:commentsIds) - durable IDs
        var commentsIdsPart =
            main.GetPartsOfType<WordprocessingCommentsIdsPart>().FirstOrDefault()
            ?? main.AddNewPart<WordprocessingCommentsIdsPart>();

        commentsIdsPart.CommentsIds ??= new DocumentFormat.OpenXml.Office2019.Word.Cid.CommentsIds();
        commentsIdsPart.CommentsIds.Save();

        // 4) commentsExtensible.xml (w16cex:commentsExtensible) - extra extensible info
        var commentsExtensiblePart =
            main.GetPartsOfType<WordCommentsExtensiblePart>().FirstOrDefault()
            ?? main.AddNewPart<WordCommentsExtensiblePart>();

        commentsExtensiblePart.CommentsExtensible ??=
            new DocumentFormat.OpenXml.Office2021.Word.CommentsExt.CommentsExtensible();

        commentsExtensiblePart.CommentsExtensible.Save();
    }

    /// <summary>
    /// Builds initials from author name (up to 3 characters)
    /// </summary>
    private static string BuildInitials(string author)
    {
        var parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0])));
        return initials.Length > 3 ? initials[..3] : initials;
    }
}
