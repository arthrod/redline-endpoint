// Diagnostic script to examine WmlComparer output
using Docxodus;
using System.IO.Compression;
using System.Xml.Linq;

class Diagnose
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Docxodus WmlComparer Diagnostic ===\n");

        // Load test documents
        var original = new WmlDocument("/home/user/redline-endpoint/original.docx");
        var modified = new WmlDocument("/home/user/redline-endpoint/modified.docx");

        Console.WriteLine($"Original: {original.DocumentByteArray.Length} bytes");
        Console.WriteLine($"Modified: {modified.DocumentByteArray.Length} bytes\n");

        // Run comparison with default settings (no cleaning)
        var settings = new WmlComparerSettings
        {
            AuthorForRevisions = "Redline",
            DetailThreshold = 0
        };

        Console.WriteLine("Running WmlComparer.Compare()...\n");
        var result = WmlComparer.Compare(original, modified, settings);

        Console.WriteLine($"Result: {result.DocumentByteArray.Length} bytes\n");

        // Save raw output (no cleaning)
        var rawPath = "/home/user/redline-endpoint/output_raw.docx";
        result.SaveAs(rawPath);
        Console.WriteLine($"Saved raw output to: {rawPath}\n");

        // Examine the raw output
        Console.WriteLine("=== Examining Raw Output ===\n");
        ExamineDocx(rawPath);

        // Now examine original for comparison
        Console.WriteLine("\n=== Examining Original (for comparison) ===\n");
        ExamineDocx("/home/user/redline-endpoint/original.docx");
    }

    static void ExamineDocx(string path)
    {
        Console.WriteLine($"File: {path}");
        using var archive = ZipArchive.OpenRead(path);

        // List all entries
        Console.WriteLine($"\nEntries ({archive.Entries.Count} total):");
        foreach (var entry in archive.Entries.Take(20))
        {
            Console.WriteLine($"  {entry.FullName} ({entry.Length} bytes)");
        }

        // Check document.xml.rels for relationship issues
        var relsEntry = archive.GetEntry("word/_rels/document.xml.rels");
        if (relsEntry != null)
        {
            Console.WriteLine("\n--- word/_rels/document.xml.rels ---");
            using var stream = relsEntry.Open();
            var xdoc = XDocument.Load(stream);
            var formatted = xdoc.ToString();
            Console.WriteLine(formatted);

            // Check for problematic patterns
            var relationships = xdoc.Root?.Elements().ToList() ?? [];
            foreach (var rel in relationships)
            {
                var id = rel.Attribute("Id")?.Value ?? "";
                var target = rel.Attribute("Target")?.Value ?? "";

                // Flag issues
                var issues = new List<string>();
                if (!id.StartsWith("rId"))
                    issues.Add($"Non-standard ID format: {id}");
                if (target.StartsWith("/"))
                    issues.Add($"Absolute path: {target}");

                if (issues.Count > 0)
                {
                    Console.WriteLine($"\n  ⚠️  Relationship issues for Id='{id}':");
                    foreach (var issue in issues)
                        Console.WriteLine($"      - {issue}");
                }
            }
        }

        // Check document.xml for pt14 namespace
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry != null)
        {
            Console.WriteLine("\n--- word/document.xml (first 2000 chars) ---");
            using var stream = docEntry.Open();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            Console.WriteLine(content[..Math.Min(2000, content.Length)]);

            // Check for pt14 references
            if (content.Contains("pt14"))
            {
                Console.WriteLine("\n  ⚠️  Found pt14 namespace references!");
                // Count occurrences
                var count = content.Split("pt14").Length - 1;
                Console.WriteLine($"      - {count} occurrences of 'pt14'");
            }

            // Check for powertools namespace
            if (content.Contains("powertools"))
            {
                Console.WriteLine("\n  ⚠️  Found powertools namespace references!");
            }
        }

        // Check _rels/.rels (package level)
        var pkgRels = archive.GetEntry("_rels/.rels");
        if (pkgRels != null)
        {
            Console.WriteLine("\n--- _rels/.rels ---");
            using var stream = pkgRels.Open();
            var xdoc = XDocument.Load(stream);
            Console.WriteLine(xdoc.ToString());
        }

        // Check Content_Types
        var contentTypes = archive.GetEntry("[Content_Types].xml");
        if (contentTypes != null)
        {
            Console.WriteLine("\n--- [Content_Types].xml ---");
            using var stream = contentTypes.Open();
            var xdoc = XDocument.Load(stream);
            Console.WriteLine(xdoc.ToString());
        }
    }
}
