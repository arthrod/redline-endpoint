# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install native dependencies for SkiaSharp (used by Docxodus)
RUN apt-get update && apt-get install -y \
    libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Expose port
EXPOSE 9898

# Set environment variables
ENV ASPNETCORE_URLS=http://+:9898
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "RedlineApi.dll"]
