# Obsync server

The server is a single ASP.NET Core 10 application. It uses EF Core with SQLite for metadata and `FileSystemObjectStore` for SHA-256 content objects. `DATA_PATH` controls the directory containing `sync.db` and `objects/`.

Run from the repository root:

```powershell
dotnet run --project server/src/ObsidianSync.Server/ObsidianSync.Server.csproj --urls http://localhost:8080
```

Local development reads the clearly non-production key from `appsettings.json`. Set `JWT_SIGNING_KEY` in the environment to override that value; production deployments should inject it from a secret rather than using the checked-in development key.

New registration is disabled unless `REGISTRATION_KEY` (or `Registration:Key`) is configured. Account creation must send that value in the `X-Registration-Key` header. Remove the environment variable and restart after bootstrapping the intended users; JWT login for existing users remains available.

Set `ADMIN_USERNAME` and `ADMIN_PASSWORD` (or `Admin:UserName` and `Admin:Password` in configuration) to bootstrap an administrator. The password is hashed with ASP.NET Core Identity’s standard password hasher and is only used when creating the configured account; it is not replaced on every restart. If an existing local admin password must be replaced, set `ADMIN_RESET_PASSWORD=true` (or `Admin:ResetPassword=true`) for one restart, then unset it. Sign in at `/admin/` to view statistics, create users, reset passwords, and assign whole-vault Editor or ReadOnly access.

The REST contract, revision semantics, idempotency rules, and conflict strategy are documented in the root [README](../README.md) and [implementation plan](../IMPLEMENTATION_PLAN.md). Integration tests use isolated temporary data directories and exercise the end-to-end HTTP protocol.
