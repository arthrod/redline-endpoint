using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run <docx-file>");
    return;
}

var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
using var doc = WordprocessingDocument.Open(args[0], false);
var errors = validator.Validate(doc).ToList();
Console.WriteLine($"Found {errors.Count} validation errors:");
foreach (var error in errors.Take(30))
{
    Console.WriteLine($"  - {error.Description}");
    Console.WriteLine($"    Part: {error.Part?.Uri}");
    Console.WriteLine($"    XPath: {error.Path?.XPath}");
    Console.WriteLine();
}
