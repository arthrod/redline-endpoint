# Redline API

A FastEndpoints-based .NET API that compares two DOCX documents and returns a redlined document with tracked changes.

## Tech Stack

- **.NET 10** with FastEndpoints 7.x
- **Docxodus** (WmlComparer) for document comparison
- **DocumentFormat.OpenXml** for document manipulation
- JWT Bearer authentication (configured but currently `AllowAnonymous`)

## Running Locally

```bash
dotnet run --urls "http://localhost:5000"
```

## API Usage

```bash
curl -X POST http://localhost:5000/api/compare \
  -F "Original=@original.docx" \
  -F "Modified=@modified.docx" \
  -F "Author=Your Name" \
  --output redlined.docx
```

## Docker

```bash
docker build -t redline-api .
docker run -p 8080:8080 -e Jwt__Secret="your-secret-key" redline-api
```

## Known Issues

The Docxodus/WmlComparer library produces output that causes Word to show "unreadable content" warnings. The `CleanDocument` method in `CompareDocumentsEndpoint.cs` attempts to fix these issues by:

1. Removing `pt14:` PowerTools namespace and attributes
2. Fixing GUID-style relationship IDs (e.g., `R76e2e2db...` → `rId8`)
3. Converting absolute paths to relative paths (`/word/footnotes.xml` → `footnotes.xml`)

See `attempted_methods.md` for debugging history.

## Project Structure

```
├── Program.cs                           # FastEndpoints + JWT setup
├── Endpoints/Compare/
│   └── CompareDocumentsEndpoint.cs      # Main comparison endpoint
├── appsettings.json                     # Configuration
├── Dockerfile                           # Docker deployment
├── original.docx                        # Test file
└── modified.docx                        # Test file
```

## Enabling Authentication

1. Remove `AllowAnonymous()` from `CompareDocumentsEndpoint.cs`
2. Update `Jwt:Secret` in `appsettings.json`
3. Include `Authorization: Bearer <token>` header in requests
