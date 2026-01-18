# Redline API - Development Guide

Document comparison API using .NET 10 + FastEndpoints + Docxodus.

## Quick Reference

```bash
# Run locally
dotnet run

# Test endpoint
curl -X POST http://localhost:5003/api/compare \
  -F "Original=@original.docx" \
  -F "Modified=@modified.docx" \
  --output redlined.docx

# Build Docker
docker build -t redline-api .
docker run -p 8080:8080 redline-api
```

## Architecture

- **Single endpoint**: `POST /api/compare` - accepts two DOCX files, returns redlined document
- **Core logic**: `Endpoints/Compare/CompareDocumentsEndpoint.cs`
- **Comparison engine**: Docxodus WmlComparer
- **Post-processing**: `CleanDocument()` fixes OOXML compatibility issues

## Key Implementation Details

The Docxodus library output requires post-processing for Word compatibility:

1. **Move operations** → Converted to del/ins (moves are fragile in OOXML)
2. **PowerTools namespace** → Removed (pt14: attributes cause warnings)
3. **Relationship IDs** → Normalized from GUIDs to rId format
4. **Paths** → Converted from absolute to relative

## Testing

Sample files `original.docx` and `modified.docx` are included for testing.
