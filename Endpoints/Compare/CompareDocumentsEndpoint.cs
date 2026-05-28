using FastEndpoints;
using Docxodus;

namespace RedlineApi.Endpoints.Compare;

public class CompareRequest
{
    public IFormFile Original { get; set; } = null!;
    public IFormFile Modified { get; set; } = null!;
    public string? Author { get; set; }
    public double? DetailThreshold { get; set; }
    public bool? CaseInsensitive { get; set; }
}

public class CompareDocumentsEndpoint : Endpoint<CompareRequest>
{
    public override void Configure()
    {
        Post("/api/compare");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CompareRequest req, CancellationToken ct)
    {
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

            var originalDoc = new WmlDocument("original.docx", originalStream.ToArray());
            var modifiedDoc = new WmlDocument("modified.docx", modifiedStream.ToArray());

            WmlDocument result;
            if (req.DetailThreshold.HasValue || req.CaseInsensitive.HasValue)
            {
                var settings = new WmlComparerSettings
                {
                    AuthorForRevisions = req.Author ?? "Docxodus",
                    DetailThreshold = req.DetailThreshold ?? 0.15,
                    CaseInsensitive = req.CaseInsensitive ?? false
                };

                result = WmlComparer.Compare(originalDoc, modifiedDoc, settings);
            }
            else
            {
                var settings = new WmlComparerSettings
                {
                    AuthorForRevisions = req.Author ?? "Docxodus"
                };

                result = WmlComparer.Compare(originalDoc, modifiedDoc, settings);
            }

            await Send.BytesAsync(
                result.DocumentByteArray,
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
}
