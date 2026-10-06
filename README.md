# MatPaper

MatPaper is a self-hosted, Dockerized document management system (DMS) — a lightweight
alternative to [Paperless-NGX](https://github.com/paperless-ngx/paperless-ngx) built on a
modern .NET stack.

## Quick start

Ready-made images are published to the GitHub Container Registry:

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/matpaper:latest` | `main` | releases |
| `ghcr.io/real-ttx/matpaper:nightly` | `dev` | the newest features |

### 1. Just run it

Copy this into `docker-compose.yml` and start it – nothing else needed:

```yaml
services:
  matpaper:
    image: ghcr.io/real-ttx/matpaper:latest
    restart: unless-stopped
    ports:
      - "4994:8080"
    volumes:
      - matpaper-data:/data
      - matpaper-storage:/storage
    depends_on:
      - db

  db:
    image: postgres:16
    restart: unless-stopped
    environment:
      POSTGRES_PASSWORD: matpaper
      POSTGRES_DB: matpaper
    volumes:
      - matpaper-db:/var/lib/postgresql/data

volumes:
  matpaper-data:
  matpaper-storage:
  matpaper-db:
```

```bash
docker compose up -d
```

Open **http://localhost:4994**. The first visit asks you to create the administrator account.
The database is only reachable from inside the compose network, so its password needs no
change for a default setup. The three volumes hold everything: `matpaper-data` the
configuration, session keys and thumbnails, `matpaper-storage` the filed documents,
`matpaper-db` the database. An update is `docker compose pull && docker compose up -d`; the
database is migrated on start.

Then, in the app: **System → Settings** for the public address and time zone,
**System → Connections** for a NAS, mailbox or cloud drive, **System → Import tasks** to
collect documents from a mailbox or folder.

### 2. With the folders of your NAS

Documents go to a *storage location*. To use a folder of your NAS instead of the Docker volume,
mount it below `/storage` and add it once under **System → Storage locations**. A Synology
keeps its shares under `/volume1`:

```yaml
  matpaper:
    volumes:
      - matpaper-data:/data
      - /volume1/documents:/storage/documents   # left: the NAS, right: what MatPaper sees
```

In the app enter the path **inside the container**: `/storage/documents`. Shares of other
systems work the same way (TrueNAS `/mnt/<pool>`, Unraid `/mnt/user`, plain Linux anywhere);
only the left side of the colon changes. A network share needs no mount at all: add it under
**System → Connections** as an SMB connection and use it as a storage location.

To keep the review inbox off the container host, point `MATPAPER_INBOX` at a mounted folder
(see [Data](#data)).

### 3. From source

```bash
docker compose -f docker-compose.dev.yml up -d --build
```

- **App:** http://localhost:4994
- **pgAdmin:** http://localhost:4995 (login `admin@matpaper.example.com` / `matpaper`)

## Screenshots

Taken from a demo instance with made-up documents (German UI; English is built in).

| | |
|---|---|
| ![Documents](docs/images/documents.png) | ![Inbox](docs/images/inbox.png) |
| **Documents** — thumbnails, full-text search and filters | **Inbox** — everything not yet filed, taken over with one click |
| ![Document detail](docs/images/document-detail.png) | ![Connections](docs/images/connections.png) |
| **Detail** — preview, metadata, sharing | **Connections** — NAS, mailboxes and cloud drives, defined once |

**Themes.** Light, dark or system, five colour schemes and seven accent colours — every
combination is checked for readable contrast. Each user picks their own look (it follows the
account to every device); the administrator sets the default for everyone else and for the
sign-in page.

| | |
|---|---|
| ![Paper, amber](docs/images/theme-paper-amber.png) | ![Black (OLED), violet](docs/images/theme-oled-violet.png) |
| **Paper** with amber | **Black (OLED)** with violet |
| ![High contrast, petrol](docs/images/theme-contrast-teal.png) | ![Dark, blue](docs/images/theme-dark-blue.png) |
| **High contrast** with petrol | **Dark** with blue |

<p>
  <img src="docs/images/appearance.png" alt="Appearance settings" width="49%" />
  <img src="docs/images/mobile-documents.png" alt="Mobile view" width="22%" />
</p>

## Tech stack

- **.NET 10** (ASP.NET Core **Razor Pages**)
- **PostgreSQL** (via Npgsql / EF Core 10) with full-text search (`tsvector` + `pg_trgm`)
- Runs entirely in **Docker**

## Features (evolving)

- Document storage with correspondents, tags, projects and document types
- One inbox: everything that is not in the archive yet waits there — uploads, mail and
  folder imports, and files a storage search found — and is taken over with one click
- Storage locations on local folders, **SMB/CIFS shares**, **Google Drive** or **OneDrive** —
  no host mount needed; every location has a folder picker
- **Connections** (System → Connections): a NAS, a mailbox or a cloud drive is defined once
  — with a password / app password or **OAuth** (Gmail, Office 365, Google Drive, OneDrive) —
  and reused by storage locations and import tasks
- Full-text search over title and OCR text
- **E-invoices** (XRechnung, ZUGFeRD, Factur-X): amounts, due date, VAT ID, IBAN and buyer are read from
  the structured data; a bare XRechnung XML becomes a readable PDF with the XML kept next to it
- **Metadata files** (`.matpaper.json`) next to the documents, so an archive can be read back into a
  fresh database
- Import (IMAP / POP3 / filesystem / SMB) and export/backup tasks
- Themes per user, instance settings page (public address, time zone, default language),
  German and English UI
- Per-user ownership, sharing and a common area; cookie-based authentication with roles

See [PLAN.md](PLAN.md) for the full roadmap and phase breakdown.

## Data

Application data (config, data-protection keys, thumbnails) is persisted in the `/data`
volume.

**Inbox.** The inbox lists every document that has not been taken into the archive yet,
no matter where it came from. Two kinds of entry share the list:

- *Staged* — uploaded, camera-scanned or imported. The file waits in a local staging
  folder (`/data/inbox` by default) and is moved into a storage location when you take the
  document over. To keep that folder off the container host (e.g. on a NAS), mount a share
  into the container and point `MATPAPER_INBOX` (or `Storage.InboxPath` in
  `/data/config/app.json`) at it; see the commented `matpaper-inbox` example in the compose
  files. It must be a local path inside the container because OCR and thumbnail generation
  run directly on it.
- *Found* — discovered by a storage search. The file already lies inside a storage
  location and nothing is copied. Taking it over either adopts it where it is or re-files
  it by the location's path template. Files you do not want are ignored, not deleted: the
  entry keeps the next search from offering them again.

Nothing is read from disk during a search, so pointing MatPaper at a large archive is
cheap. Text recognition for found files starts when you take one over or press "analyze".

A storage location can run its search on a schedule, like the import and export tasks. Every
run, manual or scheduled, is recorded in the task history with its log.

Documents in the common area appear in everybody's inbox and everybody may take them over or
ignore them. Deleting stays with the owner. A deleted document goes to the bin, reachable
through the "Deleted" filter on the document list, and can be restored as long as its file
still exists.

**Storage locations** (System → Storage locations) hold the filed documents. A location is
a local folder (such as the `/storage` volume or a mounted share), an SMB/CIFS share reached
over the network, or a Google Drive / OneDrive folder — the last two through a saved
[connection](#connections-and-oauth). Files are placed by the location's
path template, e.g. `{Correspondent}/{Year}/{DocumentType}/{Date} {Title}{Ext}`. Import
tasks that skip the inbox file directly into their configured location. The search icon on
a location looks through it for files MatPaper does not know yet; which extensions it picks
up, who owns the finds, whether they go to the common area and how often the search runs is
configured per location.

**What an import remembers.** An import task does not read everything again on every run.
IMAP tasks store the folder's position (UIDVALIDITY and the highest UID handled) and ask the
server only for newer messages; POP3 tasks remember the UIDLs they handled; folder and SMB tasks
remember path, size and modification time of the files they handled. If the server renumbers a
folder, or the task's folder, filters or period change, the task starts over. "Start over" on
the task does the same by hand. The content hash stays the safety net: a file that already
exists is never filed twice.

Under *Period* a task can be limited to the last N days or to items from a given date on — for
IMAP the server does that filtering, so an old mailbox is not fetched at all. The preview in
the wizard looks at the newest 200 to 5000 items (POP3: at most 500, as it has to download
each message) and shows 25 at a time; it ignores the remembered position so it always shows
what the filters would catch.

**E-invoices.** A PDF with embedded invoice data (ZUGFeRD, Factur-X) and a standalone XRechnung
`.xml` are recognised everywhere a file comes in (upload, import tasks, storage search). The invoice
facts - gross, net and VAT amount, due date, VAT ID, IBAN, buyer, buyer reference - are stored with
the document, shown in its detail view and marked with an *E-invoice* badge. A bare XML is rendered
into an A4 PDF so it behaves like any other document; the original is saved next to it as
`{file}.pdf.xml` and can be downloaded from the document. To collect e-invoices from a mailbox or
folder add `.xml` to the task's attachment extensions.

**Metadata files.** A storage location can write a `{file}.matpaper.json` next to every filed
document (System → Storage locations → *Write metadata files*). It holds title, date, type,
correspondent, project, tags, invoice data and the review state - by name, not by id. Whatever
moves, renames or deletes the document takes this file and an `.xml` companion along. Import tasks
(folder, SMB) and the storage search read the file back, so pointing MatPaper at an archive restores
everything it knew; missing types, correspondents and tags are created by name. Empty folder
placeholders follow the instance language (*Unsortiert* / *Ohne Typ* / *Ohne Titel* or *Unsorted* /
*Unfiled* / *Untitled*).

## Connections and OAuth

**Connections** (System → Connections) hold *where* something is and *how to sign in*:
SMB shares, IMAP/POP3 mailboxes, Google Drive and OneDrive. Storage locations and import tasks
pick a saved connection instead of carrying their own host and password. Secrets are encrypted
with ASP.NET Data Protection (keys in `/data/keys`) and never shown again.

Gmail, Office 365, Google Drive and OneDrive sign in with **OAuth**. There is no shared
MatPaper app — you register your own with Google Cloud or Azure, enter its client ID and
secret in the connection and press *Connect*:

1. Set the **public address** of your instance under System → Settings (or with
   `MATPAPER_PUBLIC_URL`). The page then shows the **redirect URI**
   (`<address>/System/Connections/OAuthCallback`); register exactly that with Google/Azure.
2. Google: publish the Cloud project as *In production*, otherwise the authorization expires
   after seven days. Scopes: `https://mail.google.com/` (mail) or `…/auth/drive` (Drive).
3. Microsoft: delegated `Files.ReadWrite.All` (OneDrive) or the IMAP/POP scopes (mail),
   plus `offline_access`.

Where an app password is simpler (e.g. Gmail with 2-factor), choose the password sign-in.

## Settings

System → Settings edits the public address, the time zone (used for every displayed time and
for task schedules) and the default language/theme, and shows version, data folder and
database. Changes are written to `/data/config/app.json` and apply immediately. A value pinned
by an environment variable (`MATPAPER_PUBLIC_URL`, `MATPAPER_TZ`) is shown locked.
Database, data folder and inbox folder are read at startup.
