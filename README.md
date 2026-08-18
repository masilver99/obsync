# Obsync

Obsync is a self-hosted synchronization system for Obsidian vaults. It combines a TypeScript Obsidian community plugin with a single C# / ASP.NET Core server, SQLite metadata, and filesystem-backed content-addressed storage.

The first milestone is runnable: authenticate, create or select a whole vault, register a device, upload Markdown or binary files, request incremental changes, download content, preserve history, propagate deletes/renames, and preserve stale uploads as conflict copies.

## Architecture

```text
Obsidian plugin  -- JWT + REST -->  ASP.NET Core server
       |                                  |
 local vault files                    SQLite /data/sync.db
                                      SHA-256 objects /data/objects/
```

Logical vault paths are metadata. They are normalized and never concatenated into storage paths. Content objects are addressed by SHA-256, so identical bytes are stored once. Every accepted operation receives a vault-wide revision sequence number and remains queryable in file history. Deletes are tombstones.

The concise design record is in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md). The server API is intentionally straightforward enough for another client to implement from the documented requests and responses.

## Run the server

With Docker, set a secret signing key and configure the administrator account before starting:

```powershell
$env:JWT_SIGNING_KEY = 'replace-with-a-long-random-secret'
$env:ADMIN_USERNAME = 'admin'
$env:ADMIN_PASSWORD = 'replace-with-a-long-admin-password'
# Optional: keep this set while invite-key registration is needed.
$env:REGISTRATION_KEY = 'replace-with-a-long-random-registration-key'
docker compose up --build
```

The server listens on `http://localhost:8080`. Persistent service state is under `./data`:

```text
data/
├── sync.db
└── objects/
    └── <first two hash characters>/<remaining hash>
```

Back up `data/` to back up the service. `CORS_ORIGINS` accepts a semicolon-separated allow-list and defaults to the Obsidian desktop origin.

Without Docker:

```powershell
$env:DATA_PATH = (Join-Path (Get-Location) 'data')
dotnet run --project server/src/ObsidianSync.Server/ObsidianSync.Server.csproj --urls http://localhost:8080
```

In VS Code, open either the repository root or `server/src/ObsidianSync.Server`, stop any existing debug session, build once, and press F5 using `ObsidianSync.Server (admin port 5105)`. The launch profile opens `/admin/` automatically at [http://localhost:5105/admin/](http://localhost:5105/admin/).

Local development uses the non-production JWT key in `server/src/ObsidianSync.Server/appsettings.json`. Registration is disabled unless `REGISTRATION_KEY` or `Registration:Key` is configured. The checked-in `Admin` settings are intentionally blank; set `Admin:UserName` and `Admin:Password` in local configuration, or use `ADMIN_USERNAME` and `ADMIN_PASSWORD` in Docker/GitHub Actions. Environment configuration takes precedence. The configured admin user is created with a secure password hash on first startup and is promoted if it already exists; its password is not overwritten on subsequent restarts. To deliberately replace an existing admin hash once, set `Admin:ResetPassword` or `ADMIN_RESET_PASSWORD=true`, restart, then unset it or set it back to false.

## GitHub Actions deployment

Pull requests run server tests and plugin tests/builds without production credentials. Pushes to `main` and version tags publish an immutable server image to GHCR, tagged with the commit SHA. Production deployment is a separate, manually approved workflow; its setup and one-time server contract are documented in [deploy/README.md](deploy/README.md). Runtime secrets and the persistent `data/` directory stay on the server.

Health is available at [http://localhost:8080/health](http://localhost:8080/health). It checks both SQLite connectivity and object-directory writability.

## Admin dashboard and vault management

Open [http://localhost:8080/admin/](http://localhost:8080/admin/) and sign in with the configured administrator account. The page itself is a static shell, but all dashboard data and management actions require an administrator JWT. The token is held in memory only and is cleared when the page is closed.

The dashboard shows users, devices/activity, vault membership, file counts, logical data size, content-addressed object storage usage, and the last completed sync. It can create users, set a user password, and assign an existing user Editor or ReadOnly access to an entire vault. Folder-level ACLs and owner reassignment remain out of scope.

## Build and install the plugin

```powershell
cd plugin
npm install
npm test
npm run build
```

Copy `plugin/main.js` and `plugin/manifest.json` into `<vault>/.obsidian/plugins/obsync/`, enable the plugin, enter the server URL and account credentials, then select/associate a remote vault and enable synchronization. The plugin stores a JWT and synchronization metadata in Obsidian plugin data; it does not persist the password. For a production deployment, use HTTPS and a server URL trusted by the Obsidian environment.

## Protocol essentials

All protected requests use `Authorization: Bearer <jwt>`.

- `POST /api/auth/register`: `{ "userName": "michael", "password": "..." }` plus `X-Registration-Key`; returns 403 when registration is disabled or the key is invalid.
- `POST /api/auth/login`: `{ "userName": "michael", "password": "..." }`
- `POST /api/vaults`: `{ "name": "My Vault" }`
- `POST /api/devices`: `{ "name": "Laptop" }`
- `GET /api/vaults/{id}/changes?after=0`: returns `{ currentRevision, changes[] }`; each change includes `fileId`, `revision`, `operation`, `path`, optional `oldPath`, `contentHash`, and `size`.
- `POST /api/vaults/{id}/sync/upload`: multipart fields `deviceId`, `operationId`, `path`, `baseFileRevision`, `content`.
- `POST /api/vaults/{id}/sync/delete`: `{ deviceId, operationId, path, baseFileRevision }`.
- `POST /api/vaults/{id}/sync/rename`: `{ deviceId, operationId, oldPath, path, baseFileRevision }`.
- `POST /api/vaults/{id}/sync/heartbeat`: `{ deviceId, status, lastKnownRevision, errorMessage? }`, where `status` is `started`, `completed`, or `failed`; this is activity telemetry and is not required for sync correctness.
- `GET /api/vaults/{id}/files/{fileId}/content?revision=42`: current or historical bytes.
- `GET /api/vaults/{id}/files/{fileId}/history`: revision metadata.

Administrator endpoints require a JWT issued to a configured administrator user:

- `GET /api/admin/dashboard`: aggregate overview plus user and vault rows.
- `POST /api/admin/users`: `{ userName, password, isAdmin }`.
- `POST /api/admin/users/{userId}/password`: `{ password }`.
- `PUT /api/admin/vaults/{vaultId}/members`: `{ userName, role }`, with `role` set to `Editor` or `ReadOnly`.
- `DELETE /api/admin/vaults/{vaultId}/members/{userId}`: removes non-owner membership.

Mutations are idempotent by `(vaultId, deviceId, operationId)`. Upload responses are `accepted` or `conflict`; conflict responses identify a server-created conflict-copy file and the current revision of the original. A stale delete or rename returns HTTP 409 and does not discard the client’s work.

Registration is invite-key gated. Keep `REGISTRATION_KEY` out of the repository and remove it from the running environment after creating the initial account if no more accounts should be created. Existing users can continue to log in after registration is disabled.

## Current capabilities and limitations

Implemented now:

- username/password authentication with secure ASP.NET password hashing and JWT bearer tokens;
- SQLite/WAL metadata and local content-addressed object storage;
- whole-vault Owner, Editor, and ReadOnly roles in the schema and authorization checks;
- incremental revisions, hashes, stable file IDs, history retrieval, tombstone deletes, renames, binary files, retries, and stale-upload conflict preservation;
- Obsidian event observation, persisted client state, pull/apply/upload ordering, and feedback-loop suppression;
- Docker deployment and health checks.
- config-seeded administrator bootstrap, an admin-only dashboard, user creation/password management, whole-vault membership management, and sync completion telemetry.

Deferred intentionally:

- invitation email delivery and a polished plugin sharing UI;
- a full history/restore UI, safe text 3-way merge, and read-only/editor UI affordances;
- realtime notifications, garbage collection, S3/PostgreSQL backends, a full migration system, and CRDT/live collaboration;
- mobile-specific secure credential storage beyond Obsidian’s plugin-data facilities.

## Development commands

```powershell
dotnet restore server/tests/ObsidianSync.Server.Tests/ObsidianSync.Server.Tests.csproj
dotnet build server/src/ObsidianSync.Server/ObsidianSync.Server.csproj
dotnet test server/tests/ObsidianSync.Server.Tests/ObsidianSync.Server.Tests.csproj
cd plugin; npm test; npm run build
```
