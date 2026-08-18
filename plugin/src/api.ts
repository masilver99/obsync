export interface AuthResponse {
  token: string;
  expiresUtc: string;
  user: { id: string; userName: string };
}

export interface VaultSummary {
  id: string;
  name: string;
  currentRevision: number;
  role: string;
}

export interface DeviceSummary {
  id: string;
  name: string;
  lastSeenUtc: string;
}

export interface ChangeDto {
  fileId: string;
  revision: number;
  operation: "upsert" | "delete" | "rename" | "conflictcopy";
  path: string;
  oldPath?: string;
  contentHash?: string;
  size: number;
  isConflict: boolean;
  baseFileRevision: number;
  createdUtc: string;
}

export interface ChangesResponse {
  vaultId: string;
  currentRevision: number;
  changes: ChangeDto[];
}

export interface SyncMutationResponse {
  status: "accepted" | "conflict";
  fileId: string;
  revision: number;
  path: string;
  oldPath?: string;
  contentHash?: string;
  size: number;
  currentFileRevision: number;
  currentVaultRevision: number;
  replay: boolean;
  conflictPath?: string;
}

export interface SyncHeartbeatResponse {
  sessionId: string;
  status: string;
  recordedUtc: string;
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly body: unknown
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export class ObsyncApi {
  constructor(
    private readonly serverUrl: string,
    private readonly token?: string
  ) {}

  private get baseUrl(): string {
    return this.serverUrl.replace(/\/+$/, "");
  }

  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers);
    if (this.token) {
      headers.set("Authorization", `Bearer ${this.token}`);
    }
    if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }

    const response = await fetch(`${this.baseUrl}${path}`, { ...init, headers });
    const text = await response.text();
    let body: unknown = undefined;
    if (text.length > 0) {
      try {
        body = JSON.parse(text);
      } catch {
        body = text;
      }
    }

    if (!response.ok) {
      const detail = typeof body === "object" && body !== null && "detail" in body
        ? String((body as { detail?: unknown }).detail ?? "")
        : response.statusText;
      throw new ApiError(detail || `Request failed with HTTP ${response.status}.`, response.status, body);
    }

    return body as T;
  }

  private json<T>(path: string, body: unknown, extraHeaders?: HeadersInit): Promise<T> {
    return this.request<T>(path, { method: "POST", body: JSON.stringify(body), headers: extraHeaders });
  }

  register(userName: string, password: string, registrationKey: string): Promise<AuthResponse> {
    const headers = registrationKey.trim().length > 0
      ? { "X-Registration-Key": registrationKey.trim() }
      : undefined;
    return new ObsyncApi(this.serverUrl).json<AuthResponse>("/api/auth/register", { userName, password }, headers);
  }

  login(userName: string, password: string): Promise<AuthResponse> {
    return new ObsyncApi(this.serverUrl).json<AuthResponse>("/api/auth/login", { userName, password });
  }

  listVaults(): Promise<VaultSummary[]> {
    return this.request<VaultSummary[]>("/api/vaults");
  }

  createVault(name: string): Promise<VaultSummary> {
    return this.json<VaultSummary>("/api/vaults", { name });
  }

  listDevices(): Promise<DeviceSummary[]> {
    return this.request<DeviceSummary[]>("/api/devices");
  }

  createDevice(name: string): Promise<DeviceSummary> {
    return this.json<DeviceSummary>("/api/devices", { name });
  }

  getChanges(vaultId: string, after: number, limit = 500): Promise<ChangesResponse> {
    return this.request<ChangesResponse>(`/api/vaults/${encodeURIComponent(vaultId)}/changes?after=${after}&limit=${limit}`);
  }

  heartbeat(
    vaultId: string,
    request: { deviceId: string; status: "started" | "completed" | "failed"; lastKnownRevision: number; errorMessage?: string }
  ): Promise<SyncHeartbeatResponse> {
    return this.json<SyncHeartbeatResponse>(`/api/vaults/${encodeURIComponent(vaultId)}/sync/heartbeat`, request);
  }

  async download(vaultId: string, fileId: string, revision?: number): Promise<ArrayBuffer> {
    const suffix = revision === undefined ? "" : `?revision=${revision}`;
    const response = await fetch(`${this.baseUrl}/api/vaults/${encodeURIComponent(vaultId)}/files/${encodeURIComponent(fileId)}/content${suffix}`, {
      headers: this.token ? { Authorization: `Bearer ${this.token}` } : undefined
    });
    if (!response.ok) {
      throw new ApiError(`Download failed with HTTP ${response.status}.`, response.status, await response.text());
    }
    return response.arrayBuffer();
  }

  upload(
    vaultId: string,
    deviceId: string,
    operationId: string,
    path: string,
    baseFileRevision: number,
    content: ArrayBuffer
  ): Promise<SyncMutationResponse> {
    const form = new FormData();
    form.append("deviceId", deviceId);
    form.append("operationId", operationId);
    form.append("path", path);
    form.append("baseFileRevision", String(baseFileRevision));
    const fileName = path.slice(path.lastIndexOf("/") + 1) || "content";
    form.append("content", new Blob([new Uint8Array(content)]), fileName);
    return this.request<SyncMutationResponse>(`/api/vaults/${encodeURIComponent(vaultId)}/sync/upload`, {
      method: "POST",
      body: form
    });
  }

  delete(vaultId: string, request: { deviceId: string; operationId: string; path: string; baseFileRevision: number }): Promise<SyncMutationResponse> {
    return this.json<SyncMutationResponse>(`/api/vaults/${encodeURIComponent(vaultId)}/sync/delete`, request);
  }

  rename(vaultId: string, request: { deviceId: string; operationId: string; oldPath: string; path: string; baseFileRevision: number }): Promise<SyncMutationResponse> {
    return this.json<SyncMutationResponse>(`/api/vaults/${encodeURIComponent(vaultId)}/sync/rename`, request);
  }
}
