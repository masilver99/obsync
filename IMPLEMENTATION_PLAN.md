# Obsync implementation plan

## Project structure

- `server/src/ObsidianSync.Server`: one ASP.NET Core application with minimal REST endpoints.
- `server/tests/ObsidianSync.Server.Tests`: WebApplicationFactory integration tests against temporary SQLite/object-store data.
- `plugin/src`: normal Obsidian TypeScript plugin, bundled to `main.js` for manual installation.
- `docker-compose.yml`: the default single-container deployment with one persistent `/data` mount.

## Data and storage

SQLite stores users, vaults, whole-vault memberships, devices, stable logical file identities, file revisions, tombstones, invitations, idempotency records, and sync activity sessions. A vault revision is a monotonically increasing sequence shared by all file changes. A configured admin account is securely password-hashed during bootstrap. The initializer includes small compatibility steps for the admin and sync-session additions because the first milestone uses `EnsureCreated` rather than a migration package.

Content is written to `/data/objects/<first-two-hash-characters>/<remaining-hash>` after streaming SHA-256 calculation. Database metadata is committed only after the object has reached its final path; an orphaned object is safer than metadata pointing to missing content. Client paths are normalized and case-folded for lookup, but are never used as filesystem paths.

## Protocol

1. `POST /api/auth/register` or `POST /api/auth/login` returns a JWT.
2. Authenticated clients create/list devices and vaults.
3. `GET /api/vaults/{vaultId}/changes?after={revision}` returns ordered changes and the current vault revision.
4. `POST /api/vaults/{vaultId}/sync/upload` accepts multipart fields `deviceId`, `operationId`, `path`, `baseFileRevision`, and binary `content`.
5. `POST .../sync/delete` and `POST .../sync/rename` accept JSON requests with the same device/operation/base-revision fields.
6. `GET .../files/{fileId}/content?revision={revision}` retrieves current or historical bytes.
7. `GET .../files/{fileId}/history` lists revision metadata.
8. `POST .../sync/heartbeat` records best-effort started/completed/failed activity for operational stats; it is not part of correctness.

Administrator clients use `GET /api/admin/dashboard` and the related `/api/admin/users` and `/api/admin/vaults/{vaultId}/members` endpoints. These require the `obsync_admin` claim issued after config bootstrap. The server also serves a vanilla HTML dashboard at `/admin/`.

All mutating operations require a client-generated stable `operationId`. Replaying it returns the original result rather than creating another logical revision. A file mutation compares `baseFileRevision` to the file’s current server revision. A stale upload is stored as a new conflict-copy file, preserving both byte streams; stale delete/rename operations are rejected for review.

## Client state and ordering

The plugin persists the last processed vault revision, remote file IDs, each file’s last-known hash/revision, and pending renames in Obsidian plugin data. It pulls remote changes first, applies them with event suppression, processes renames, scans local files by hash, uploads local changes, and pulls once more. Timestamps are not used as synchronization truth.

## Deliberate first-milestone limits

The initial slice has whole-vault membership roles, an owner-only member update endpoint, an admin-only user/vault management surface, and invite-key-gated registration. It has no invitation email workflow or polished plugin sharing UI. REST is authoritative; realtime notifications, 3-way merge, read-only UI, garbage collection, PostgreSQL, S3, and CRDT/live collaboration are deferred.
