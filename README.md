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
- Review inbox: new documents wait in a staging area with auto-suggested metadata; on
  confirmation they are filed into a storage location using a human-readable path template
- Storage locations on local folders **or SMB/CIFS shares** (no host mount needed)
- Full-text search over title and OCR text
- Import (IMAP / POP3 / filesystem / SMB) and export/backup tasks
- Per-user ownership, sharing and a common area; cookie-based authentication with roles

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
volume.

**Inbox (staging).** Uploaded, scanned and imported documents first land in the review
inbox. Their files are kept in a local staging folder — `/data/inbox` by default — and are
only moved into a storage location when you confirm them. To keep the inbox off the
container host (e.g. on a NAS), mount a share into the container and point
`MATPAPER_INBOX` (or `Storage.InboxPath` in `/data/config/app.json`) at it; see the
commented `matpaper-inbox` example in the compose files. The folder must be a local path
inside the container because OCR and thumbnail generation run directly on it.

**Storage locations** (System → Storage locations) hold the filed documents. A location is
either a local folder (such as the `/storage` volume or a mounted share) or an SMB/CIFS
share reached over the network with a saved credential. Files are placed by the location's
path template, e.g. `{Correspondent}/{Year}/{DocumentType}/{Date} {Title}{Ext}`. Import
tasks that skip the inbox file directly into their configured location.
