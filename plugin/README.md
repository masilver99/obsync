# Obsync Obsidian plugin

This is a normal TypeScript Obsidian community plugin. It uses the Obsidian Vault event API for create/modify/delete/rename notifications and persists the server revision, remote file IDs, hashes, and pending renames with `loadData`/`saveData`.

Build and test:

```powershell
npm install
npm test
npm run build
```

For manual installation, copy `main.js` and `manifest.json` into `<vault>/.obsidian/plugins/obsync/`. Configure the server URL, username, password, and—only during registration—the server registration key. Sign in or register, choose a remote vault, and enable sync. Password and registration-key inputs are used only for their authentication actions; neither is saved in plugin data. The server rejects registration when no registration key is configured.

The client pulls before uploading, uses content hashes instead of timestamps, writes remote content with event suppression, and keeps local/server conflict copies rather than silently overwriting edits. REST remains the correctness path; realtime notifications are not required.
