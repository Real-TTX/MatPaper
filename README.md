# MatPaper

MatPaper is a self-hosted, Dockerized document management system (DMS) — a lightweight
alternative to [Paperless-NGX](https://github.com/paperless-ngx/paperless-ngx) built on a
modern .NET stack.

## Tech stack

- **.NET 10** (ASP.NET Core **Razor Pages**)
- **PostgreSQL** (via Npgsql / EF Core 10) with full-text search (`tsvector` + `pg_trgm`)
- Runs entirely in **Docker**

## Features (evolving)

- Document storage with correspondents, tags, projects and document types
- Full-text search over title and OCR text
- Import (IMAP / POP3 / filesystem) and export/backup tasks
- Cookie-based authentication with roles

See [PLAN.md](PLAN.md) for the full roadmap and phase breakdown.

## Running (development)

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

Then open:

- **App:** http://localhost:4994
- **pgAdmin:** http://localhost:4995 (login `admin@matpaper.local` / `matpaper`)

## Running (release)

The release compose file pulls the prebuilt image from GHCR and omits pgAdmin:

```bash
docker compose -f docker-compose.release.yml up -d
```

## Data

Application data (config, data-protection keys, thumbnails) is persisted in the `/data`
volume. Uploaded documents are stored under `/storage`.
