using Docxodus;
using DocumentFormat.OpenXml.Packaging;
using System.Xml.Linq;

// PowerTools namespace that causes Word warnings
XNamespace Pt14 = "http://powertools.codeplex.com/2011";

// Load documents
var original = new WmlDocument("../original.docx");
var modified = new WmlDocument("../modified.docx");

// Configure comparison settings
var settings = new WmlComparerSettings
{
    AuthorForRevisions = "Legal Review",
    DetailThreshold = 0
};

// Compare documents
var result = WmlComparer.Compare(original, modified, settings);

// Get list of revisions (with move detection)
var revisions = WmlComparer.GetRevisions(result, settings);
foreach (var rev in revisions)
{
    if (rev.RevisionType == WmlComparer.WmlComparerRevisionType.Moved)
        Console.WriteLine($"Moved (group {rev.MoveGroupId}): {rev.Text}");
    else
        Console.WriteLine($"{rev.RevisionType}: {rev.Text}");
}

// Clean PowerTools namespace and save
var cleanedBytes = CleanPowerToolsNamespace(result.DocumentByteArray, Pt14);
File.WriteAllBytes("redlined_code.docx", cleanedBytes);

Console.WriteLine("\nRedlined document saved as: redlined_code.docx");

// Cleanup function to remove PowerTools pt14 namespace
static byte[] CleanPowerToolsNamespace(byte[] docBytes, XNamespace pt14)
{
    using var stream = new MemoryStream();
    stream.Write(docBytes, 0, docBytes.Length);
    stream.Position = 0;

    using (var doc = WordprocessingDocument.Open(stream, true))
    {
        if (doc.MainDocumentPart != null)
        {
            CleanPart(doc.MainDocumentPart, pt14);
            if (doc.MainDocumentPart.StyleDefinitionsPart != null)
                CleanPart(doc.MainDocumentPart.StyleDefinitionsPart, pt14);
            if (doc.MainDocumentPart.NumberingDefinitionsPart != null)
                CleanPart(doc.MainDocumentPart.NumberingDefinitionsPart, pt14);
            if (doc.MainDocumentPart.DocumentSettingsPart != null)
                CleanPart(doc.MainDocumentPart.DocumentSettingsPart, pt14);
            if (doc.MainDocumentPart.FootnotesPart != null)
                CleanPart(doc.MainDocumentPart.FootnotesPart, pt14);
            if (doc.MainDocumentPart.EndnotesPart != null)
                CleanPart(doc.MainDocumentPart.EndnotesPart, pt14);
            foreach (var headerPart in doc.MainDocumentPart.HeaderParts)
                CleanPart(headerPart, pt14);
            foreach (var footerPart in doc.MainDocumentPart.FooterParts)
                CleanPart(footerPart, pt14);
        }
    }
    return stream.ToArray();
}

static void CleanPart(OpenXmlPart part, XNamespace pt14)
{
    using var partStream = part.GetStream(FileMode.Open, FileAccess.ReadWrite);
    var xdoc = XDocument.Load(partStream);

    foreach (var element in xdoc.Descendants())
    {
        var pt14Attrs = element.Attributes()
            .Where(a => a.Name.Namespace == pt14)
            .ToList();
        foreach (var attr in pt14Attrs)
            attr.Remove();
    }

    var root = xdoc.Root;
    if (root != null)
    {
        var nsDeclarations = root.Attributes()
            .Where(a => a.IsNamespaceDeclaration && a.Value == pt14.NamespaceName)
            .ToList();
        foreach (var ns in nsDeclarations)
            ns.Remove();

        var mcIgnorable = root.Attribute(XName.Get("Ignorable", "http://schemas.openxmlformats.org/markup-compatibility/2006"));
        if (mcIgnorable != null)
        {
            var values = mcIgnorable.Value.Split(' ')
                .Where(v => v != "pt14")
                .ToArray();
            mcIgnorable.Value = string.Join(" ", values);
        }
    }

    partStream.SetLength(0);
    xdoc.Save(partStream);
}
