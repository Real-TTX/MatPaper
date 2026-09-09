# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (copy csproj first for layer caching)
COPY src/MatPaper/MatPaper.csproj ./MatPaper/
RUN dotnet restore ./MatPaper/MatPaper.csproj

# Copy the rest of the source and publish
COPY src/MatPaper ./MatPaper
RUN dotnet publish ./MatPaper/MatPaper.csproj -c Release -o /app/publish

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# OCR + image/rendering runtime dependencies
#  - tesseract-ocr (+ deu/eng): OCR engine invoked via CLI
#  - libfontconfig1: required by SkiaSharp native (thumbnail encoding)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        tesseract-ocr \
        tesseract-ocr-deu \
        tesseract-ocr-eng \
        libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ARG APP_VERSION=local
ENV APP_VERSION=${APP_VERSION}
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "MatPaper.dll"]
