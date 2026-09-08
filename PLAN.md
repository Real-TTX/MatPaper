# MatPaper — Implementierungsplan

> Dieser Plan ist die verbindliche Vorgabe für die Implementierung.
> Er ist in Phasen aufgeteilt, die nacheinander umgesetzt werden.
> Jede Phase endet mit einem lauffähigen Zustand (Container baut & startet).

---

## 1. Projektziel

MatPaper ist eine Docker-basierte Alternative zu Paperless-NGX: ein Dokumenten-Management-System (DMS) mit OCR, Volltextsuche, Korrespondenzen, Tags, Projekten, Import/Export-Tasks und **human-readable Dateiablage auf nativen Speicherorten** (z. B. NAS-Freigaben).

Wichtigster konzeptioneller Unterschied zu Paperless: Die Dateien liegen nicht in einem opaken Blob-Store, sondern lesbar strukturiert im Dateisystem — der Speicherort bleibt auch ohne MatPaper nutzbar.

---

## 2. Tech-Stack (fix, nicht verhandelbar)

| Bereich | Entscheidung |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core **Razor Pages** (kein Blazor, kein SPA-Framework). Hinweis: ursprünglich war .NET 8 vorgesehen; auf dem Build-Rechner ist SDK 10 + EF-Tools 10 installiert, daher .NET 10 → lokaler Build == Container-Build. |
| Datenbank | PostgreSQL 16 (Npgsql + EF Core, Migrations werden beim App-Start automatisch angewendet) |
| Configs | JSON-Dateien im Daten-Volume (`/data/config/*.json`) |
| Container | Dockerfile auf `mcr.microsoft.com/dotnet/aspnet:10.0`-Basis, Multi-Stage-Build (`sdk:10.0` → `aspnet:10.0`) |
| Port | **4994** (Host → Container) |
| Compose | `docker-compose.dev.yml` (mit pgAdmin) und `docker-compose.release.yml` — so einfach wie möglich, keine unnötigen ENV-Variablen |
| OCR | Tesseract CLI im Image (`tesseract-ocr` + `deu` + `eng`), Textextraktion aus PDFs zuerst mit PdfPig; nur wenn kein Textlayer vorhanden → Seiten rendern + Tesseract |
| PDF-Rendering / Thumbnails | PDFium via NuGet `PDFtoImage` (erste Seite → WebP/PNG-Thumbnail) |
| E-Mail-Import | MailKit (IMAP + POP3) |
| PDF-Erzeugung | **QuestPDF** (kein Headless-Browser!). Zwei Einsatzfälle: (1) Mail→PDF: HTML-Body mit **AngleSharp** parsen (Absätze, Listen, Links, Tabellen, Inline-Bilder) und als einheitliches „Archiv-Brief“-Layout rendern (Kopf: Von/An/Datum/Betreff); Original-`.eml` optional mit ablegen. (2) PWA-Kamera-Scan: mehrere Fotos → ein PDF. Hinweis: QuestPDF Community-Lizenz ist kostenlos < 1 Mio. $ Jahresumsatz. |
| Scheduler | Eigener `BackgroundService` + NuGet `Cronos` für Cron-Ausdrücke (kein Hangfire/Quartz — Stack einfach halten) |
| Volltextsuche | PostgreSQL `tsvector` (Konfiguration `german` + `simple` kombiniert), GIN-Index |
| Auth | ASP.NET Core Cookie-Auth, lokale Benutzer (Passwort-Hash: `PasswordHasher<T>`), vorbereitet für spätere Entra ID (Auth-Schema abstrahieren) |
| PWA | `manifest.webmanifest` + Service Worker (Offline-Shell, "Add to Homescreen", Kamera-Scan über `<input type=file capture>`) |

---

## 3. Projektstruktur

```
MatPaper/
├── PLAN.md
├── README.md
├── .gitignore                     (VisualStudio/.NET Standard)
├── Dockerfile
├── docker-compose.dev.yml
├── docker-compose.release.yml
├── .github/workflows/docker.yml
└── src/
    └── MatPaper/
        ├── MatPaper.csproj
        ├── Program.cs
        ├── Data/                  (DbContext, Entities, Migrations)
        ├── Services/              (Business-Logik, siehe unten)
        ├── Pages/                 (Razor Pages, Struktur = Menüstruktur)
        │   ├── Shared/            (_Layout, Controls als Partials/ViewComponents)
        │   ├── Dashboard/
        │   ├── Documents/
        │   ├── Correspondents/
        │   ├── Tags/
        │   ├── Projects/
        │   ├── System/            (Users, Roles, StorageLocations, Tasks, Settings)
        │   └── Account/           (Login, Logout)
        └── wwwroot/
            ├── css/ js/ icons/
            ├── manifest.webmanifest
            └── service-worker.js
```

**Ein einziges Projekt** (`MatPaper.csproj`) — keine Solution mit fünf Layern. Business-Funktionen in Service-Klassen unter `Services/`, mehrere zusammengehörige Funktionen dürfen in einer Klasse leben. Microsoft C# Guidelines beachten, wenig Verschachtelung.

---

## 4. Datenmodell (PostgreSQL)

Konventionen für **jede** Tabelle:
- Name PascalCase, PK immer `Id BIGINT` (Identity).
- Audit-Felder: `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`.
- Soft-Delete wo sinnvoll: `UpdateState` (0 = Deleted, 1 = Created, 2 = Updated).
- Sicherheitsrelevante Schlüssel zusätzlich als `Token UUID` (nie die Id nach außen geben).

### Tabellen

**User** — `Id, Username, DisplayName, Email, PasswordHash, RoleId, IsActive, Audit`
**Role** — `Id, Name` (Seed: Admin, User, Anonymous)
**UserSession** — `Id, Token (uuid), UserId, ExpiresAt, LastSeenAt, UserAgent, Audit` → Sessions überleben Container-Restarts (Cookie referenziert Token, Validierung gegen DB). Zusätzlich: DataProtection-Keys nach `/data/keys` persistieren, sonst sind Cookies nach Restart ungültig.

**StorageLocation** — `Id, Name, RootPath, PathTemplate, IsDefault, UpdateState, Audit`
- `RootPath`: nativer Pfad im Container (gemountete NAS-Freigabe etc.)
- `PathTemplate`: human-readable Ablageschema, z. B. `{Correspondent}/{Year}/{DocumentType}/{Date} {Title}{Ext}`

**Correspondent** — `Id, Name, MatchPattern (nullable, für Auto-Zuordnung), Notes, UpdateState, Audit`
**Tag** — `Id, Name, Color, UpdateState, Audit`
**Project** — `Id, Name, Color, UpdateState, Audit` (im UI als `#ProjectName`)
**DocumentType** — `Id, Name, UpdateState, Audit` (Seed: Rechnung, Angebot, Anfrage, Auftragsbestätigung, Lieferschein, Vertrag, Mahnung, Kontoauszug, Bescheid, Sonstiges)

**Document**
```
Id, Token (uuid, für Share-Links & Thumbnail-URLs),
Title, DocumentDate (fix vorhandenes Feld), DocumentTypeId, CorrespondentId, ProjectId,
StorageLocationId, RelativePath, OriginalFileName, FileSize, ContentHash (SHA256, Duplikat-Erkennung),
OcrText, SearchVector (tsvector, generated column, GIN-Index),
ThumbnailPath, PageCount, OcrState (0=Pending, 1=Done, 2=Failed),
UpdateState, Audit
```
**DocumentTag** — `Id, DocumentId, TagId` (n:m)

**ImportTask** — `Id, Name, Type (0=Imap, 1=Pop3, 2=Filesystem), IsEnabled, CronExpression, SettingsJson, UpdateState, Audit`
- `SettingsJson` enthält typabhängige Einstellungen: Host/Port/SSL/User/Passwort, Ordner, Filter (Absender-Regex, Betreff-Regex), Modus (Anlagen importieren / Mail als PDF), Aktion nach Import (löschen/verschieben/markieren), Ziel-Defaults (StorageLocationId, CorrespondentId, Tags, DocumentTypeId), bei Filesystem: Quellordner, Pattern, "nach Import löschen".

**ExportTask** — `Id, Name, Type (0=Backup, 1=Export), IsEnabled, CronExpression, SettingsJson, UpdateState, Audit`
- Backup: pg_dump + Configs + optional Dokumente als tar.gz nach Zielpfad, Retention (n Stück behalten).

**TaskRun** — `Id, TaskType (0=Import, 1=Export), TaskId, StartedAt, FinishedAt, State (0=Running, 1=Success, 2=Failed), ItemsProcessed, Log (text)` — Historie/Protokoll für die Task-Seiten.

**ShareLink** — `Id, Token (uuid), DocumentId, ExpiresAt (nullable), UpdateState, Audit` — anonymer Lesezugriff auf ein einzelnes Dokument (Rolle „Anonymous“).

### Volltextsuche
`SearchVector` = gewichtete Kombination aus `Title` (A), `OcrText` (B), Korrespondent-/Tag-Namen denormalisiert (C). Suche über `websearch_to_tsquery` + Trigram-Fallback (`pg_trgm`) für Teilwörter.

---

## 5. Kernkonzept: Speicherorte & human-readable Ablage

1. **Verwalten:** CRUD unter System → Speicherorte. Ein Speicherort = gemounteter Pfad + Pfad-Template. Ein Speicherort ist Default für neue Dokumente.
2. **Ablage:** Beim Import/Upload wird die Datei nach Template abgelegt, z. B. `Telekom/2026/Rechnung/2026-09-01 Mobilfunk September.pdf`. Bei Metadaten-Änderung (Korrespondent, Datum, Typ, Titel) wird die Datei **verschoben/umbenannt** (Konsistenz Datei ↔ Metadaten). Kollisionen: Suffix ` (2)`.
3. **Bestehende Speicherorte einbinden:** Ein Speicherort kann gescannt werden („Inhalte erkennen“). Gefundene, noch nicht registrierte Dateien (Abgleich per Pfad + ContentHash) landen in einer **Import-Inbox-Ansicht**: dort kann man pro Datei/Auswahl Korrespondent, Typ, Tags zuordnen und importieren — wahlweise „an Ort und Stelle belassen“ (Pfad wird übernommen, keine Umbenennung) oder „nach Template einsortieren“.
4. **Export-Schedules werden dadurch nicht ersetzt** — Backup/Export-Tasks bleiben eigenständig (Punkt 8).
5. Thumbnails & Systemdaten liegen NICHT im Speicherort, sondern im Daten-Volume (`/data/thumbnails/{Token}.webp`) — der Speicherort bleibt sauber.

---

## 6. Dokument-Pipeline

Upload / Import → immer derselbe Weg (`DocumentIngestService`):
1. Datei annehmen, SHA256 bilden, Duplikat-Check (Hinweis statt Doppelimport).
2. Nach Template im Ziel-Speicherort ablegen.
3. Document-Datensatz anlegen (`OcrState = Pending`).
4. Hintergrund-Queue (`Channel<long>` + BackgroundService):
   a. Textlayer mit PdfPig extrahieren; wenn leer → Seiten via PDFium rendern → Tesseract (`deu+eng`).
   b. Bilder (jpg/png/tiff) → direkt Tesseract; Office-Formate: nur ablegen, kein OCR (MVP).
   c. Thumbnail der ersten Seite erzeugen (WebP, ~400 px Breite).
   d. `PageCount`, `OcrText`, `OcrState` aktualisieren.
5. Auto-Zuordnung: Correspondent per `MatchPattern` (Regex/Contains gegen OCR-Text), Datum-Heuristik (erstes plausibles Datum im Text) als Vorschlag.

---

## 7. Auth, Rollen, Sessions

- **Login:** lokale Anmeldung (Username + Passwort). Erster Start ohne User → Setup-Seite „Admin anlegen“.
- **Rollen:** Admin (alles inkl. System), User (Dokumente/Stammdaten), Anonymous (nur via ShareLink-Token auf genau ein Dokument).
- **Sessions:** Cookie enthält Session-Token (uuid) → Validierung gegen `UserSession`-Tabelle (Sliding Expiration). DataProtection-Keys unter `/data/keys` → Restart-fest.
- **Entra ID später:** Auth hinter `SignInService` kapseln, damit ein zweites Schema (OpenIdConnect) ergänzbar ist, ohne Seiten anzufassen.

---

## 8. Import- & Export-Tasks

Gemeinsamer Scheduler (`TaskSchedulerService : BackgroundService`): prüft jede Minute alle aktivierten Tasks gegen ihren Cron-Ausdruck (Cronos), schreibt `TaskRun`-Protokolle, „Jetzt ausführen“-Button pro Task.

**Import — E-Mail (IMAP/POP3, MailKit):** Verbindungstest-Button, Filter (Absender/Betreff-Regex), Modus: Anlagen (PDF/Bilder) importieren und/oder Mail als PDF (AngleSharp + QuestPDF, siehe Stack; kommt in Phase 7), Aktion nach Import, Defaults (Speicherort, Korrespondent, Tags, Typ) → Pipeline aus Punkt 6.
**Import — Filesystem:** Quellordner (Mount), Datei-Pattern, optional rekursiv, „nach Import löschen/verschieben“, Defaults wie oben.
**Export — Backup:** Cron, Zielpfad (Mount), Inhalt: DB-Dump (`pg_dump` im App-Container via `Npgsql`-COPY oder `pg_dump`-Binary im Image), `/data/config`, optional Dokumente. Retention.

---

## 9. UI

### Layout
- **Linkes Menü** (einklappbar, mobil als Off-Canvas): Dashboard, Documents, Correspondents, Tags (mit Tag-Liste als Untereinträge), Projects (mit `#Projekt`-Untereinträgen), System (nur Admin).
- **Unten links im Menü:** Account-Name, Logout, Theme-Umschalter (System / Dark / Bright).
- **Rechter Inhaltsbereich:** Header mit Breadcrumb, darunter Seiteninhalt.
- Scrollbalken: ausgeblendet bzw. nur beim Scrollen sichtbar, im Design der Anwendung (CSS `scrollbar-width: thin` + Webkit-Styling).
- Eigenes, schlankes CSS (CSS Custom Properties für Theming) — kein Bootstrap-Vollpaket nötig; wenn Framework, dann nur minimal genutzt.

### Wiederverwendbare Controls (eine Code-Basis, mehrfach pro Seite nutzbar!)
Als **ViewComponents/TagHelper** mit eindeutigem `id`-Prefix pro Instanz:
- **Table**: Spaltendefinition, Sortierung, Zeilen-Link, leere-Liste-Hinweis, mobil als Karten-Fallback.
- **Toolbar** (immer ÜBER der Tabelle): Suchtext, Filter-Dropdowns, Sortierung.
- **Pagination**: direkt unter der Tabelle.
- **Listen-Aktionen**: UNTERHALB der Pagination, linksbündig; Delete-Aktionen mit deutlichem Abstand rechts davon.
- **Form**: Feld-Layout, Validierung, abhängige Felder nur anzeigen wenn benötigt (z. B. IMAP-Felder nur bei Typ=IMAP — per kleinem JS-Helper `data-show-when`).
- **TabBar**: für Seiten mit mehreren Bereichen.
- **Buttons in Formularen:** eine Zeile, positiv → negativ: `Save, Back, <SPACE> Delete`.
- **Icons** überall wo sinnvoll (z. B. Lucide/Feather als Inline-SVG-Partial, kein Icon-Font-CDN).

### CRUD-Muster (für ALLE Stammdaten gleich)
Liste (`/tags`) → Bearbeiten/Neu auf **separater Unterseite** (`/tags/edit/{id}`, `/tags/edit` für neu). Kein Inline-Edit, keine Modals für CRUD.

### Seiten
- **Dashboard:** Kennzahlen (Dokumente gesamt, letzte 7 Tage, OCR-Queue, fehlgeschlagene Tasks), zuletzt hinzugefügte Dokumente (Thumbnails).
- **Documents:** Liste mit Thumbnails, Toolbar (Suche = Volltext, Filter: Korrespondent, Typ, Tag, Projekt, Datum von/bis, Speicherort), Upload-Button. Detailseite: Vorschau (Thumbnail/Seiten), Metadaten-Form, Tags, Share-Link erzeugen, Download, Delete (soft).
- **Correspondents / Tags / Projects / DocumentTypes:** Standard-CRUD nach Muster. Klick auf Tag/Projekt im Menü → vorgefilterte Dokumentenliste.
- **System:** Users, StorageLocations (+ „Scannen/Inbox“), Import-Tasks, Export-Tasks, Task-Historie, Settings.
- **Account:** Login (zentriert, ohne Menü), Setup-Seite beim Erststart.

### PWA
Manifest + Icons + Service Worker (App-Shell-Cache). „Dokument scannen“: mobiler Upload über Kamera (`accept="image/*" capture="environment"`, mehrere Bilder → serverseitig zu einem PDF zusammenfassen mit QuestPDF).

---

## 10. Docker & Compose

**Dockerfile** (Multi-Stage): `sdk:10.0` build → `aspnet:10.0` runtime + `apt-get install tesseract-ocr tesseract-ocr-deu tesseract-ocr-eng`. App lauscht auf 8080, `VOLUME /data`.

**docker-compose.dev.yml** — Services so schlicht wie möglich:
```yaml
services:
  matpaper:
    build: .
    ports: ["4994:8080"]
    volumes:
      - matpaper-data:/data
      - matpaper-storage:/storage        # Beispiel-Speicherort für Dev
    depends_on: [db]
  db:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: matpaper
      POSTGRES_DB: matpaper
    volumes:
      - matpaper-db:/var/lib/postgresql/data
  pgadmin:
    image: dpage/pgadmin4
    ports: ["4995:80"]
    environment:
      PGADMIN_DEFAULT_EMAIL: admin@matpaper.local
      PGADMIN_DEFAULT_PASSWORD: matpaper
volumes: { matpaper-data:, matpaper-db:, matpaper-storage:, }
```
**docker-compose.release.yml**: wie dev, aber `image: ghcr.io/<owner>/matpaper:latest` statt build, ohne pgadmin.

Connection-String & App-Settings kommen aus `/data/config/app.json` (JSON-Config-Prinzip); beim ersten Start wird die Datei mit Defaults erzeugt (DB-Host `db`, Passwort `matpaper`).

**Live-Reload/Testen:** immer `docker compose -f docker-compose.dev.yml up -d --build` — Container neu bauen und Stack redeployen, kein dotnet watch.

---

## 11. Git, Branches, CI, Versionierung

- Branches: `main` (Release), `dev` (Development). Initial auf `main` committen, dann `dev` abzweigen; gearbeitet wird auf `dev`.
- Commits/Pushes **ausschließlich unter dem Namen des Users** (Matthias Schmoldt) — keine Co-Authored-By-Trailer, keine "Generated with"-Zeilen.
- **GitHub Action** `.github/workflows/docker.yml`:
  - Trigger: Push auf `main` und `dev`.
  - Buildnumber = `github.run_number` (auto-increment), Builddate = `yyyyMMdd`.
  - `main` → Tag `1.0.<run_number>-<builddate>` + `latest`
  - `dev` → Tag `nightly-<run_number>-<builddate>` + `nightly`
  - Push nach GHCR (`ghcr.io/<owner>/matpaper`), Login via `GITHUB_TOKEN`.
  - Version wird als Build-Arg ins Image gereicht und im UI-Footer/System angezeigt.
- Lokaler Build: Version `local-<builddate>` (Default im Dockerfile, wenn kein Build-Arg gesetzt).

---

## 12. Umsetzungsphasen (Reihenfolge für die Implementierung)

**Phase 1 — Gerüst & Infrastruktur**
Projekt anlegen, Dockerfile, beide Compose-Files, .gitignore, README. Leere Razor-Pages-App mit Layout (Menü links, Breadcrumb, Theme-Umschalter, Scrollbar-Styling). EF Core + DbContext + alle Entities + initiale Migration + Auto-Migrate beim Start. JSON-Config unter `/data/config/app.json`. GitHub Action. ✔ Container startet auf :4994, leeres Dashboard sichtbar.

**Phase 2 — Auth & Benutzer**
Setup-Seite (erster Admin), Login/Logout, Cookie + UserSession-Tabelle, DataProtection nach `/data/keys`, Rollen-Enforcement, System → Users CRUD (Muster: Liste + Unterseite). ✔ Login restart-fest.

**Phase 3 — Stammdaten-CRUD & UI-Controls**
Controls bauen (Table, Toolbar, Pagination, Form, TabBar, Buttons, Icons). Damit: Correspondents, Tags, Projects, DocumentTypes (mit Seed-Daten), StorageLocations. ✔ Alle Stammdaten pflegbar, UI-Guidelines erfüllt.

**Phase 4 — Dokumente**
Upload, Ingest-Pipeline (Ablage nach Template, Hash, OCR-Queue, Thumbnail), Dokumentenliste mit Toolbar/Filtern/Volltextsuche, Detailseite mit Metadaten-Edit (inkl. Datei-Move bei Änderungen), Download, Soft-Delete, Dashboard-Kennzahlen. ✔ Kernnutzen komplett.

**Phase 5 — Speicherort-Scan & Inbox**
„Scannen“-Funktion pro Speicherort, Inbox-Ansicht mit Massen-Zuordnung, Import „an Ort und Stelle“ oder „einsortieren“. ✔ Bestehende Ablagen einbindbar.

**Phase 6 — Tasks**
Scheduler, Import-Tasks (Filesystem zuerst, dann IMAP/POP3 mit Filtern), Export/Backup-Tasks, TaskRun-Historie, „Jetzt ausführen“. ✔ Automatisierung komplett.

**Phase 7 — Share-Links, PWA, Feinschliff**
ShareLink (anonymer Zugriff), PWA (Manifest, Service Worker, Kamera-Scan → PDF via QuestPDF), Mail-Body→PDF (AngleSharp + QuestPDF), Mobile-Feinschliff, Versionsanzeige. ✔ Release-Kandidat auf `main`.

---

## 13. Verbindliche Guidelines (Kurzfassung für jede Phase)

**UI:** CRUD = Liste + separate Edit-Unterseite. Toolbar (Suche/Filter/Sortierung) immer über der Tabelle; Pagination direkt darunter; Listen-Aktionen darunter linksbündig, Delete mit Abstand. Formular-Buttons in einer Zeile positiv→negativ (`Save, Back, … Delete`). Abhängige Felder nur bei Bedarf sichtbar. Icons wo sinnvoll. Mobile-tauglich. Controls haben genau eine Code-Basis und funktionieren mehrfach pro Seite.

**Code:** Wenig Verschachtelung, Microsoft C# Guidelines, Business-Funktionen dürfen gebündelt in einer Klasse liegen.

**DB:** Tabellen PascalCase, PK `Id BIGINT`, Audit-Felder überall, `UpdateState` bei Soft-Delete, sicherheitsrelevante Schlüssel als zusätzliches `Token UUID`.

**Betrieb:** Testen = Container neu bauen + Stack redeployen. Daten (Config, Keys, Thumbnails, DB) in Volumes.

**Git:** Nur unter dem Namen des Users committen/pushen, keine KI-Attribution.
