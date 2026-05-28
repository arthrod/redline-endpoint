# Redline API

A high-performance .NET API that compares two DOCX documents and returns a redlined document with tracked changes. Built with FastEndpoints for minimal overhead and maximum throughput.

## Features

- Compare two Word documents and generate a redlined version with tracked changes
- Automatic cleanup of comparison artifacts for Word compatibility
- Converts complex move operations to simple del/ins for maximum compatibility
- JWT Bearer authentication support (configurable)
- Docker-ready deployment

## Tech Stack

- **.NET 10** - Latest LTS runtime
- **FastEndpoints** - High-performance minimal API framework
- **Docxodus (WmlComparer)** - Document comparison engine
- **DocumentFormat.OpenXml** - OOXML manipulation

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker (optional, for containerized deployment)

### Run Locally

```bash
dotnet run
```

The API will start on `http://localhost:9898` by default.

### API Usage

```bash
curl -X POST http://localhost:9898/api/compare \
  -F "Original=@original.docx" \
  -F "Modified=@modified.docx" \
  -F "Author=Your Name" \
  --output redlined.docx
```

#### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `Original` | File | Yes | The original document |
| `Modified` | File | Yes | The modified document |
| `Author` | String | No | Author name for tracked changes (default: "User") |

#### Response

Returns the redlined DOCX file with tracked changes showing all differences between the two documents.

## Docker Deployment

### Build

```bash
docker build -t redline-api .
```

### Run

```bash
docker run -p 9898:9898 redline-api
```

### With Authentication

```bash
docker run -p 9898:9898 -e Jwt__Secret="your-secret-key-min-32-chars" redline-api
```

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `ASPNETCORE_URLS` | Server binding URLs | `http://+:9898` |
| `ASPNETCORE_ENVIRONMENT` | Environment (Development/Production) | `Production` |
| `Jwt__Secret` | JWT signing secret (min 32 chars) | - |

### Enable Authentication

1. Set the `Jwt__Secret` environment variable
2. Remove `AllowAnonymous()` from `CompareDocumentsEndpoint.cs`
3. Include `Authorization: Bearer <token>` header in requests

## Project Structure

```
├── Program.cs                           # Application entry point
├── Endpoints/Compare/
│   └── CompareDocumentsEndpoint.cs      # Document comparison endpoint
├── appsettings.json                     # Configuration
├── Dockerfile                           # Container definition
├── original.docx                        # Sample original document
└── modified.docx                        # Sample modified document
```

## License

MIT
