using FastEndpoints;
using Docxodus;
using DocumentFormat.OpenXml.Packaging;
using System.Xml.Linq;
using System.Text;

namespace RedlineApi.Endpoints.Compare;

/// <summary>
/// Custom UTF-8 encoding that reports "UTF-8" (uppercase) as its WebName.
/// This ensures XML declarations use encoding="UTF-8" instead of encoding="utf-8".
/// </summary>
public class UppercaseUtf8Encoding : UTF8Encoding
{
    public UppercaseUtf8Encoding() : base(false) { } // false = no BOM

    public override string WebName => "UTF-8";
}

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
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";

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

            // Clean the output to remove PowerTools internal namespace/attributes and fix uniqueness constraints
            // Note: We only clean pt14 namespace. GUID-style relationship IDs are valid per OOXML spec.
            // DO NOT modify relationship IDs without also updating all references in document content!
            var cleanedBytes = CleanDocument(result.DocumentByteArray, msg => Logger.LogInformation(msg));

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
    /// Cleans the document by removing PowerTools namespace and fixing Word uniqueness constraints.
    /// </summary>
    private static byte[] CleanDocument(byte[] docBytes, Action<string>? log = null)
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

            // Fix Word-enforced uniqueness constraints (wp:docPr/@id, w14:paraId/textId)
            FixWordUniquenessConstraints(doc, log);
        }

        stream.Position = 0;
        var result = stream.ToArray();

        // Post-process to fix relationship absolute paths using ZipArchive
        result = FixRelationshipPathsInZip(result, log);

        return result;
    }

    /// <summary>
    /// Fixes absolute paths in .rels files using ZipArchive.
    /// Converts /word/something.xml to something.xml.
    /// </summary>
    private static byte[] FixRelationshipPathsInZip(byte[] docBytes, Action<string>? log)
    {
        using var inputStream = new MemoryStream(docBytes);
        using var outputStream = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(inputStream, System.IO.Compression.ZipArchiveMode.Read))
        using (var outputArchive = new System.IO.Compression.ZipArchive(outputStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            int fixedCount = 0;
            var relsNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");

            foreach (var entry in archive.Entries)
            {
                var newEntry = outputArchive.CreateEntry(entry.FullName, System.IO.Compression.CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var newEntryStream = newEntry.Open();

                if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    // Process .rels file
                    var xdoc = XDocument.Load(entryStream, LoadOptions.PreserveWhitespace);
                    bool changed = false;

                    foreach (var rel in xdoc.Descendants(relsNs + "Relationship"))
                    {
                        var targetAttr = rel.Attribute("Target");
                        if (targetAttr == null)
                            continue;

                        var target = targetAttr.Value;

                        // Check if it's an absolute path (starts with /)
                        if (target.StartsWith("/word/"))
                        {
                            // Convert /word/something.xml to something.xml
                            targetAttr.Value = target.Substring(6); // Remove "/word/"
                            fixedCount++;
                            changed = true;
                        }
                    }

                    // Save with proper settings (no BOM)
                    var settings = new System.Xml.XmlWriterSettings
                    {
                        Encoding = new UppercaseUtf8Encoding(),
                        Indent = false,
                        OmitXmlDeclaration = false,
                        NewLineHandling = System.Xml.NewLineHandling.None
                    };
                    using var writer = System.Xml.XmlWriter.Create(newEntryStream, settings);
                    xdoc.Save(writer);
                }
                else
                {
                    // Copy other files as-is
                    entryStream.CopyTo(newEntryStream);
                }
            }

            log?.Invoke($"Fixed relationship paths. Converted={fixedCount} absolute paths to relative.");
        }

        return outputStream.ToArray();
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

    /// <summary>
    /// Fixes Word-enforced uniqueness constraints that are not caught by schema validation.
    /// These include wp:docPr/@id and w14:paraId/textId duplicates.
    /// </summary>
    private static void FixWordUniquenessConstraints(WordprocessingDocument doc, Action<string>? log)
    {
        FixDocPrIds(doc, log);
        FixDuplicateParaIds(doc, log);
    }

    /// <summary>
    /// Fixes wp:docPr/@id uniqueness across all XML parts.
    /// wp:docPr/@id must be unique across the whole DOCX, not just within a single part.
    /// Collisions commonly happen between headers/footers and the main document when merging/comparing.
    /// </summary>
    private static void FixDocPrIds(WordprocessingDocument doc, Action<string>? log)
    {
        int nextId = 1;
        int updated = 0;

        foreach (var part in EnumerateXmlParts(doc))
        {
            if (!TryLoadXDocument(part, out var xdoc))
                continue;

            bool changed = false;

            foreach (var docPr in xdoc.Descendants(WP + "docPr"))
            {
                // id is an unqualified attribute on wp:docPr
                var idAttr = docPr.Attribute("id");
                if (idAttr == null)
                {
                    docPr.SetAttributeValue("id", nextId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    idAttr.Value = nextId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                nextId++;
                updated++;
                changed = true;
            }

            if (changed)
                SaveXDocument(part, xdoc);
        }

        log?.Invoke($"Fixed wp:docPr/@id uniqueness. Updated={updated}, NextId={nextId}.");
    }

    /// <summary>
    /// Fixes duplicate w14:paraId and w14:textId attributes within each XML part.
    /// These must be unique within their scope and less than 0x80000000.
    /// </summary>
    private static void FixDuplicateParaIds(WordprocessingDocument doc, Action<string>? log)
    {
        int fixedPara = 0;
        int fixedText = 0;

        foreach (var part in EnumerateXmlParts(doc))
        {
            if (!TryLoadXDocument(part, out var xdoc))
                continue;

            bool changed = false;

            // Uniqueness is specified "within the document part", so per-part sets are reasonable.
            var usedPara = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in xdoc.Descendants().Attributes(W14 + "paraId").ToList())
            {
                if (!usedPara.Add(attr.Value))
                {
                    attr.Value = GenerateUniqueHex(usedPara);
                    fixedPara++;
                    changed = true;
                }
            }

            var usedText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in xdoc.Descendants().Attributes(W14 + "textId").ToList())
            {
                if (!usedText.Add(attr.Value))
                {
                    attr.Value = GenerateUniqueHex(usedText);
                    fixedText++;
                    changed = true;
                }
            }

            if (changed)
                SaveXDocument(part, xdoc);
        }

        log?.Invoke($"Fixed duplicate w14:paraId/textId. paraIdFixed={fixedPara}, textIdFixed={fixedText}.");
    }

    /// <summary>
    /// Generates a unique hex string that is not in the used set.
    /// Values are constrained to be less than 0x80000000 per OOXML spec.
    /// </summary>
    private static string GenerateUniqueHex(HashSet<string> used)
    {
        Span<byte> bytes = stackalloc byte[4];

        while (true)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            uint value = BitConverter.ToUInt32(bytes);

            value &= 0x7FFFFFFF; // ensure < 0x80000000
            if (value == 0)
                continue;

            var hex = value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            if (used.Add(hex))
                return hex;
        }
    }

    /// <summary>
    /// Enumerates all XML parts in the document package.
    /// </summary>
    private static IEnumerable<OpenXmlPart> EnumerateXmlParts(WordprocessingDocument doc)
    {
        var visited = new HashSet<OpenXmlPart>();
        var queue = new Queue<OpenXmlPartContainer>();
        queue.Enqueue(doc);

        while (queue.Count > 0)
        {
            var container = queue.Dequeue();

            foreach (var idPartPair in container.Parts)
            {
                var part = idPartPair.OpenXmlPart;
                if (part == null || !visited.Add(part))
                    continue;

                // Most OpenXML XML parts end with +xml or /xml
                if (!string.IsNullOrWhiteSpace(part.ContentType) &&
                    part.ContentType.EndsWith("xml", StringComparison.OrdinalIgnoreCase))
                {
                    yield return part;
                }

                if (part is OpenXmlPartContainer childContainer)
                    queue.Enqueue(childContainer);
            }
        }
    }

    /// <summary>
    /// Safely loads an XDocument from an OpenXmlPart.
    /// </summary>
    private static bool TryLoadXDocument(OpenXmlPart part, out XDocument xdoc)
    {
        try
        {
            using var s = part.GetStream(FileMode.Open, FileAccess.Read);
            xdoc = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch
        {
            xdoc = new XDocument();
            return false;
        }
    }

    /// <summary>
    /// Saves an XDocument back to an OpenXmlPart without pretty-print indentation.
    /// </summary>
    private static void SaveXDocument(OpenXmlPart part, XDocument xdoc)
    {
        using var s = part.GetStream(FileMode.Create, FileAccess.Write);
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UppercaseUtf8Encoding(),
            Indent = false,
            OmitXmlDeclaration = false,
            NewLineHandling = System.Xml.NewLineHandling.None
        };

        using var writer = System.Xml.XmlWriter.Create(s, settings);
        xdoc.Save(writer);
    }
}
