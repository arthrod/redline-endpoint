using FastEndpoints;
using Docxodus;
using Clippit;
using Clippit.Word;

namespace RedlineApi.Endpoints.Compare;

public class CompareRequest
{
    public IFormFile Original { get; set; } = null!;
    public IFormFile Modified { get; set; } = null!;
    public string? Author { get; set; }
}

public class CompareDocumentsEndpoint : Endpoint<CompareRequest>
{
    public override void Configure()
    {
        Post("/api/compare");
        AllowFileUploads();
        // Requires Bearer token authentication
    }

    public override async Task HandleAsync(CompareRequest req, CancellationToken ct)
    {
        if (!req.Original.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
            !req.Modified.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            AddError("Both files must be .docx format");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        try
        {
            using var originalStream = new MemoryStream();
            using var modifiedStream = new MemoryStream();

            await req.Original.CopyToAsync(originalStream, ct);
            await req.Modified.CopyToAsync(modifiedStream, ct);

            // Step 1: Use Docxodus for document comparison
            var originalDoc = new Docxodus.WmlDocument("original.docx", originalStream.ToArray());
            var modifiedDoc = new Docxodus.WmlDocument("modified.docx", modifiedStream.ToArray());

            var settings = new Docxodus.WmlComparerSettings
            {
                AuthorForRevisions = req.Author ?? "Redline API",
                DetailThreshold = 0
            };

            var result = Docxodus.WmlComparer.Compare(originalDoc, modifiedDoc, settings);
            Logger.LogInformation("Docxodus comparison complete, document size: {Size} bytes", result.DocumentByteArray.Length);

            // Step 2: Use Clippit DocumentBuilder to rebuild the document
            var repairedBytes = RepairWithClippit(result.DocumentByteArray);
            Logger.LogInformation("Clippit repair complete, document size: {Size} bytes", repairedBytes.Length);

            await Send.BytesAsync(
                repairedBytes,
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
    /// Uses Clippit DocumentBuilder to rebuild the document.
    /// Clippit is a modern fork of OpenXmlPowerTools for .NET 8/10.
    /// </summary>
    private static byte[] RepairWithClippit(byte[] docBytes)
    {
        // Create a Clippit WmlDocument from the byte array
        var sourceDoc = new Clippit.Word.WmlDocument("source.docx", docBytes);

        // Use DocumentBuilder to rebuild the document
        var sources = new List<ISource>
        {
            new Clippit.Word.Source(sourceDoc)
        };

        var rebuiltDoc = Clippit.Word.DocumentBuilder.BuildDocument(sources);

        return rebuiltDoc.DocumentByteArray;
    }
}
