import {
  Notice,
  Plugin,
  PluginSettingTab,
  Setting,
  TAbstractFile,
  TFile
} from "obsidian";
import { ApiError, ChangeDto, ObsyncApi, SyncMutationResponse, VaultSummary } from "./api";
import { conflictPath } from "./paths";

interface LocalFileState {
  fileId?: string;
  path: string;
  revision: number;
  hash: string;
  deleted?: boolean;
}

interface PendingRename {
  fileId: string;
  oldPath: string;
  path: string;
  baseFileRevision: number;
  operationId: string;
}

interface ObsyncSettings {
  serverUrl: string;
  username: string;
  token: string;
  deviceName: string;
  deviceId: string;
  vaultId: string;
  enabled: boolean;
  lastRevision: number;
  files: Record<string, LocalFileState>;
  pendingRenames: PendingRename[];
}

const DEFAULT_SETTINGS: ObsyncSettings = {
  serverUrl: "http://localhost:8080",
  username: "",
  token: "",
  deviceName: "Obsidian device",
  deviceId: "",
  vaultId: "",
  enabled: false,
  lastRevision: 0,
  files: {},
  pendingRenames: []
};

export default class ObsyncPlugin extends Plugin {
  settings: ObsyncSettings = { ...DEFAULT_SETTINGS, files: {}, pendingRenames: [] };
  private statusBar?: HTMLElement;
  private syncing = false;
  private scheduledSync?: number;
  private readonly suppressedPaths = new Set<string>();

  async onload(): Promise<void> {
    await this.loadSettings();

    this.statusBar = this.addStatusBarItem();
    this.setStatus(this.settings.token ? "Obsync ready" : "Obsync: sign in");
    this.addRibbonIcon("refresh-cw", "Sync vault now", () => void this.syncNow());
    this.addCommand({
      id: "sync-now",
      name: "Sync vault now",
      callback: () => void this.syncNow()
    });
    this.addSettingTab(new ObsyncSettingTab(this.app, this));

    this.registerEvent(this.app.vault.on("create", file => this.onLocalChange(file.path)));
    this.registerEvent(this.app.vault.on("modify", file => this.onLocalChange(file.path)));
    this.registerEvent(this.app.vault.on("delete", file => this.onLocalChange(file.path)));
    this.registerEvent(this.app.vault.on("rename", (file, oldPath) => void this.onLocalRename(file, oldPath)));

    if (this.settings.enabled && this.settings.token) {
      window.setTimeout(() => void this.syncNow(), 500);
    }
  }

  async loadSettings(): Promise<void> {
    const stored = (await this.loadData()) as Partial<ObsyncSettings> | null;
    this.settings = {
      ...DEFAULT_SETTINGS,
      ...stored,
      files: { ...DEFAULT_SETTINGS.files, ...(stored?.files ?? {}) },
      pendingRenames: [...(stored?.pendingRenames ?? [])]
    };
  }

  async saveSettings(): Promise<void> {
    await this.saveData(this.settings);
  }

  async login(userName: string, password: string): Promise<void> {
    const auth = await new ObsyncApi(this.settings.serverUrl).login(userName, password);
    this.settings.username = userName;
    this.settings.token = auth.token;
    await this.saveSettings();
    this.setStatus("Obsync signed in");
    new Notice("Obsync signed in.");
  }

  async registerAccount(userName: string, password: string, registrationKey: string): Promise<void> {
    const auth = await new ObsyncApi(this.settings.serverUrl).register(userName, password, registrationKey);
    this.settings.username = userName;
    this.settings.token = auth.token;
    await this.saveSettings();
    this.setStatus("Obsync account created");
    new Notice("Obsync account created and signed in.");
  }

  async listVaults(): Promise<VaultSummary[]> {
    return this.api().listVaults();
  }

  async selectVault(vaultId: string): Promise<void> {
    this.settings.vaultId = vaultId;
    this.settings.lastRevision = 0;
    this.settings.files = {};
    this.settings.pendingRenames = [];
    await this.saveSettings();
    this.setStatus("Remote vault selected");
  }

  async syncNow(): Promise<void> {
    if (this.syncing) {
      return;
    }
    if (!this.settings.token) {
      new Notice("Sign in to Obsync before syncing.");
      return;
    }

    this.syncing = true;
    this.setStatus("Obsync: syncing…");
    let syncApi: ObsyncApi | undefined;
    try {
      await this.ensureSyncTarget();
      syncApi = this.api();
      await this.recordHeartbeat(syncApi, "started");
      await this.pullChanges();
      await this.processPendingRenames();
      await this.uploadLocalChanges();
      await this.pullChanges();
      await this.saveSettings();
      await this.recordHeartbeat(syncApi, "completed");
      this.setStatus(`Obsync: revision ${this.settings.lastRevision}`);
    } catch (error) {
      if (syncApi) {
        await this.recordHeartbeat(syncApi, "failed", error instanceof Error ? error.message : "Unknown synchronization error.");
      }
      const message = error instanceof Error ? error.message : "Unknown synchronization error.";
      this.setStatus("Obsync: error");
      new Notice(`Obsync sync failed: ${message}`);
      console.error("Obsync synchronization failed", error);
    } finally {
      this.syncing = false;
    }
  }

  setStatus(message: string): void {
    this.statusBar?.setText(message);
  }

  private api(): ObsyncApi {
    if (!this.settings.serverUrl || !this.settings.token) {
      throw new Error("Obsync server URL and sign-in are required.");
    }
    return new ObsyncApi(this.settings.serverUrl, this.settings.token);
  }

  private async ensureSyncTarget(): Promise<void> {
    const api = this.api();
    if (!this.settings.deviceId) {
      const devices = await api.listDevices();
      const existing = devices.find(device => device.name === this.settings.deviceName);
      const device = existing ?? await api.createDevice(this.settings.deviceName);
      this.settings.deviceId = device.id;
    }

    if (!this.settings.vaultId) {
      const vaults = await api.listVaults();
      const localName = this.app.vault.getName();
      const selected = vaults[0] ?? await api.createVault(localName);
      this.settings.vaultId = selected.id;
    }
    await this.saveSettings();
  }

  private async pullChanges(): Promise<void> {
    const api = this.api();
    while (true) {
      const response = await api.getChanges(this.settings.vaultId, this.settings.lastRevision);
      if (response.changes.length === 0) {
        this.settings.lastRevision = Math.max(this.settings.lastRevision, response.currentRevision);
        return;
      }

      for (const change of response.changes) {
        await this.applyRemoteChange(api, change);
        this.settings.lastRevision = Math.max(this.settings.lastRevision, change.revision);
      }
      await this.saveSettings();
    }
  }

  private async recordHeartbeat(api: ObsyncApi, status: "started" | "completed" | "failed", errorMessage?: string): Promise<void> {
    try {
      await api.heartbeat(this.settings.vaultId, {
        deviceId: this.settings.deviceId,
        status,
        lastKnownRevision: this.settings.lastRevision,
        errorMessage
      });
    } catch (error) {
      console.warn("Obsync sync activity notification was not recorded", error);
    }
  }

  private async applyRemoteChange(api: ObsyncApi, change: ChangeDto): Promise<void> {
    const known = this.settings.files[change.fileId] ?? this.findStateForPath(change.path) ?? (change.oldPath ? this.findStateForPath(change.oldPath) : undefined);
    if (change.operation === "upsert" || change.operation === "conflictcopy") {
      const content = await api.download(this.settings.vaultId, change.fileId, change.revision);
      const existing = this.app.vault.getAbstractFileByPath(change.path);
      if (existing instanceof TFile) {
        const localContent = await this.app.vault.readBinary(existing);
        const localHash = await hashBytes(localContent);
        if (!known || known.hash !== localHash) {
          await this.createLocalConflictCopy(change.path, localContent);
        }
      }

      await this.writeBinary(change.path, content);
      this.setRemoteState(change.fileId, {
        fileId: change.fileId,
        path: change.path,
        revision: change.revision,
        hash: await hashBytes(content),
        deleted: false
      });
      return;
    }

    if (change.operation === "delete") {
      const existing = this.app.vault.getAbstractFileByPath(change.path);
      if (existing instanceof TFile) {
        const localContent = await this.app.vault.readBinary(existing);
        const localHash = await hashBytes(localContent);
        if (!known || known.hash !== localHash) {
          await this.createLocalConflictCopy(change.path, localContent);
        }
        await this.deleteFile(existing);
      }

      this.setRemoteState(change.fileId, {
        fileId: change.fileId,
        path: change.path,
        revision: change.revision,
        hash: known?.hash ?? "",
        deleted: true
      });
      return;
    }

    if (change.operation === "rename" && change.oldPath) {
      const source = this.app.vault.getAbstractFileByPath(change.oldPath);
      const destination = this.app.vault.getAbstractFileByPath(change.path);
      if (destination && destination !== source) {
        if (destination instanceof TFile) {
          await this.createLocalConflictCopy(change.path, await this.app.vault.readBinary(destination));
        }
        await this.deleteFile(destination);
      }
      if (source) {
        await this.renameFile(source, change.path);
      }

      this.setRemoteState(change.fileId, {
        fileId: change.fileId,
        path: change.path,
        revision: change.revision,
        hash: known?.hash ?? change.contentHash ?? "",
        deleted: false
      });
    }
  }

  private async processPendingRenames(): Promise<void> {
    const api = this.api();
    for (const pending of [...this.settings.pendingRenames]) {
      try {
        const result = await api.rename(this.settings.vaultId, {
          deviceId: this.settings.deviceId,
          operationId: pending.operationId,
          oldPath: pending.oldPath,
          path: pending.path,
          baseFileRevision: pending.baseFileRevision
        });
        const state = this.settings.files[pending.fileId];
        if (state && result.status === "accepted") {
          state.path = result.path;
          state.revision = result.revision;
          state.deleted = false;
        }
        this.settings.pendingRenames = this.settings.pendingRenames.filter(item => item.operationId !== pending.operationId);
      } catch (error) {
        if (error instanceof ApiError && error.status === 409) {
          this.setStatus("Obsync: rename needs review");
          continue;
        }
        throw error;
      }
    }
  }

  private async uploadLocalChanges(): Promise<void> {
    const api = this.api();
    const presentPaths = new Set<string>();
    for (const file of this.app.vault.getFiles()) {
      presentPaths.add(file.path);
      const content = await this.app.vault.readBinary(file);
      const hash = await hashBytes(content);
      const state = this.findStateForPath(file.path);
      if (state?.fileId && !state.deleted && state.hash === hash && state.revision > 0) {
        continue;
      }

      const result = await api.upload(
        this.settings.vaultId,
        this.settings.deviceId,
        crypto.randomUUID(),
        file.path,
        state?.fileId ? state.revision : 0,
        content);
      await this.applyUploadResult(api, file.path, content, hash, state, result);
    }

    for (const state of Object.values(this.settings.files)) {
      if (!state.fileId || state.deleted || presentPaths.has(state.path)) {
        continue;
      }

      try {
        const result = await api.delete(this.settings.vaultId, {
          deviceId: this.settings.deviceId,
          operationId: crypto.randomUUID(),
          path: state.path,
          baseFileRevision: state.revision
        });
        if (result.status === "accepted") {
          state.revision = result.revision;
          state.deleted = true;
        }
      } catch (error) {
        if (error instanceof ApiError && error.status === 409) {
          this.setStatus(`Obsync: deletion conflict at ${state.path}`);
          continue;
        }
        throw error;
      }
    }
  }

  private async applyUploadResult(api: ObsyncApi, path: string, content: ArrayBuffer, hash: string, previous: LocalFileState | undefined, result: SyncMutationResponse): Promise<void> {
    if (result.status === "accepted") {
      this.removeState(previous);
      this.setRemoteState(result.fileId, {
        fileId: result.fileId,
        path: result.path,
        revision: result.revision,
        hash,
        deleted: false
      });
      return;
    }

    let localConflictPath = result.conflictPath ?? conflictPath(path, "local", new Date(), candidate => !!this.app.vault.getAbstractFileByPath(candidate));
    if (this.app.vault.getAbstractFileByPath(localConflictPath)) {
      localConflictPath = conflictPath(path, "local", new Date(), candidate => !!this.app.vault.getAbstractFileByPath(candidate));
    }
    if (!this.app.vault.getAbstractFileByPath(localConflictPath)) {
      await this.writeBinary(localConflictPath, content);
    }
    this.setRemoteState(result.fileId, {
      fileId: result.fileId,
      path: localConflictPath,
      revision: result.revision,
      hash,
      deleted: false
    });

    if (previous?.fileId && result.currentFileRevision > 0) {
      const remoteContent = await api.download(this.settings.vaultId, previous.fileId, result.currentFileRevision);
      await this.writeBinary(path, remoteContent);
      previous.hash = await hashBytes(remoteContent);
      previous.revision = result.currentFileRevision;
      previous.deleted = false;
    }
  }

  private async onLocalRename(file: TAbstractFile, oldPath: string): Promise<void> {
    if (this.isSuppressed(oldPath) || this.isSuppressed(file.path)) {
      return;
    }

    const state = this.findStateForPath(oldPath);
    if (state?.fileId) {
      state.path = file.path;
      this.settings.pendingRenames.push({
        fileId: state.fileId,
        oldPath,
        path: file.path,
        baseFileRevision: state.revision,
        operationId: crypto.randomUUID()
      });
      await this.saveSettings();
    }
    this.scheduleSync();
  }

  private onLocalChange(path: string): void {
    if (this.isSuppressed(path) || !this.settings.enabled || !this.settings.token) {
      return;
    }
    this.scheduleSync();
  }

  private scheduleSync(): void {
    if (this.scheduledSync !== undefined) {
      window.clearTimeout(this.scheduledSync);
    }
    this.scheduledSync = window.setTimeout(() => {
      this.scheduledSync = undefined;
      void this.syncNow();
    }, 1500);
  }

  private findStateForPath(path: string): LocalFileState | undefined {
    return Object.values(this.settings.files).find(state => state.path === path);
  }

  private setRemoteState(fileId: string, state: LocalFileState): void {
    for (const [key, existing] of Object.entries(this.settings.files)) {
      if (existing.fileId === fileId && key !== fileId) {
        delete this.settings.files[key];
      }
    }
    this.settings.files[fileId] = state;
  }

  private removeState(state: LocalFileState | undefined): void {
    if (!state) {
      return;
    }
    for (const [key, existing] of Object.entries(this.settings.files)) {
      if (existing === state || (state.fileId && existing.fileId === state.fileId)) {
        delete this.settings.files[key];
      }
    }
  }

  private async createLocalConflictCopy(path: string, content: ArrayBuffer): Promise<void> {
    const copyPath = conflictPath(path, "local", new Date(), candidate => !!this.app.vault.getAbstractFileByPath(candidate));
    await this.writeBinary(copyPath, content);
    this.setLocalState(copyPath, await hashBytes(content));
  }

  private setLocalState(path: string, hash: string): void {
    const key = `local:${crypto.randomUUID()}`;
    this.settings.files[key] = { path, revision: 0, hash };
  }

  private async writeBinary(path: string, content: ArrayBuffer): Promise<void> {
    await this.ensureParentFolder(path);
    await this.withSuppressed([path], async () => {
      const existing = this.app.vault.getAbstractFileByPath(path);
      if (existing instanceof TFile) {
        await this.app.vault.modifyBinary(existing, content);
      } else {
        await this.app.vault.createBinary(path, content);
      }
    });
  }

  private async deleteFile(file: TAbstractFile): Promise<void> {
    await this.withSuppressed([file.path], () => this.app.vault.delete(file));
  }

  private async renameFile(file: TAbstractFile, path: string): Promise<void> {
    await this.ensureParentFolder(path);
    await this.withSuppressed([file.path, path], () => this.app.vault.rename(file, path));
  }

  private async ensureParentFolder(path: string): Promise<void> {
    const segments = path.split("/");
    segments.pop();
    let current = "";
    for (const segment of segments) {
      current = current ? `${current}/${segment}` : segment;
      if (!this.app.vault.getAbstractFileByPath(current)) {
        try {
          await this.app.vault.createFolder(current);
        } catch {
          // Another event may have created the folder between the check and create.
        }
      }
    }
  }

  private async withSuppressed(paths: string[], action: () => Promise<void>): Promise<void> {
    for (const path of paths) {
      this.suppressedPaths.add(path);
    }
    try {
      await action();
    } finally {
      window.setTimeout(() => paths.forEach(path => this.suppressedPaths.delete(path)), 250);
    }
  }

  private isSuppressed(path: string): boolean {
    return this.suppressedPaths.has(path);
  }
}

async function hashBytes(content: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", content);
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

class ObsyncSettingTab extends PluginSettingTab {
  constructor(app: import("obsidian").App, private readonly plugin: ObsyncPlugin) {
    super(app, plugin);
  }

  display(): void {
    const { containerEl } = this;
    containerEl.empty();
    containerEl.createEl("h2", { text: "Obsidian Vault Sync" });

    new Setting(containerEl)
      .setName("Server URL")
      .setDesc("The HTTPS URL of the Obsync server.")
      .addText(text => text
        .setPlaceholder("http://localhost:8080")
        .setValue(this.plugin.settings.serverUrl)
        .onChange(async value => {
          this.plugin.settings.serverUrl = value.trim();
          await this.plugin.saveSettings();
        }));

    new Setting(containerEl)
      .setName("Username")
      .addText(text => text
        .setValue(this.plugin.settings.username)
        .onChange(async value => {
          this.plugin.settings.username = value.trim();
          await this.plugin.saveSettings();
        }));

    let password = "";
    new Setting(containerEl)
      .setName("Password")
      .setDesc("Used only for the current login/register action; it is not saved in plugin data.")
      .addText(text => text
        .setPlaceholder("Password")
        .onChange(value => { password = value; }));

    let registrationKey = "";
    new Setting(containerEl)
      .setName("Registration key")
      .setDesc("Required only when the server is accepting new registrations. It is not saved in plugin data.")
      .addText(text => {
        text.inputEl.type = "password";
        text.setPlaceholder("Server registration key");
        text.onChange(value => { registrationKey = value; });
      });

    new Setting(containerEl)
      .addButton(button => button.setButtonText("Log in").setCta().onClick(async () => {
        try {
          await this.plugin.login(this.plugin.settings.username, password);
          this.display();
        } catch (error) {
          new Notice(`Obsync login failed: ${error instanceof Error ? error.message : "unknown error"}`);
        }
      }))
      .addButton(button => button.setButtonText("Register").onClick(async () => {
        try {
          await this.plugin.registerAccount(this.plugin.settings.username, password, registrationKey);
          this.display();
        } catch (error) {
          new Notice(`Obsync registration failed: ${error instanceof Error ? error.message : "unknown error"}`);
        }
      }));

    new Setting(containerEl)
      .setName("Device name")
      .addText(text => text
        .setValue(this.plugin.settings.deviceName)
        .onChange(async value => {
          this.plugin.settings.deviceName = value.trim() || DEFAULT_SETTINGS.deviceName;
          await this.plugin.saveSettings();
        }));

    if (this.plugin.settings.token) {
      new Setting(containerEl)
        .setName("Remote vault")
        .setDesc(this.plugin.settings.vaultId || "No remote vault selected")
        .addButton(button => button.setButtonText("Refresh vaults").onClick(async () => {
          try {
            const vaults = await this.plugin.listVaults();
            if (vaults.length === 0) {
              new Notice("No remote vaults exist yet. Sync will create one using the local vault name.");
            } else {
              await this.plugin.selectVault(vaults[0].id);
              this.display();
            }
          } catch (error) {
            new Notice(`Unable to list vaults: ${error instanceof Error ? error.message : "unknown error"}`);
          }
        }));

      new Setting(containerEl)
        .setName("Sync enabled")
        .addToggle(toggle => toggle
          .setValue(this.plugin.settings.enabled)
          .onChange(async value => {
            this.plugin.settings.enabled = value;
            await this.plugin.saveSettings();
            if (value) {
              void this.plugin.syncNow();
            }
          }));

      new Setting(containerEl)
        .setName("Sync now")
        .setDesc(`Last server revision: ${this.plugin.settings.lastRevision}`)
        .addButton(button => button.setButtonText("Sync").setCta().onClick(() => void this.plugin.syncNow()));
    }
  }
}
