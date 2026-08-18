(() => {
  "use strict";

  let token = "";
  let dashboard = null;

  const $ = id => document.getElementById(id);
  const message = $("message");

  function setMessage(text, kind = "") {
    message.textContent = text;
    message.className = `message ${kind}`.trim();
  }

  async function api(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (token) headers.set("Authorization", `Bearer ${token}`);
    if (options.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    const response = await fetch(path, { ...options, headers });
    const text = await response.text();
    let body = null;
    if (text) {
      try { body = JSON.parse(text); } catch { body = text; }
    }
    if (!response.ok) {
      const detail = body && typeof body === "object" ? body.detail || body.error : null;
      throw new Error(detail || `Request failed with HTTP ${response.status}.`);
    }
    return body;
  }

  function escapeHtml(value) {
    return String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#039;");
  }

  function formatBytes(value) {
    if (!Number.isFinite(value) || value < 1024) return `${value || 0} B`;
    const units = ["KB", "MB", "GB", "TB"];
    let amount = value;
    let unit = "B";
    for (const next of units) {
      amount /= 1024;
      unit = next;
      if (amount < 1024 || next === units[units.length - 1]) break;
    }
    return `${amount.toFixed(amount >= 10 ? 0 : 1)} ${unit}`;
  }

  function formatDate(value) {
    if (!value) return "—";
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? "—" : date.toLocaleString();
  }

  function renderCards(overview) {
    const cards = [
      ["Users", overview.userCount],
      ["Vaults", overview.vaultCount],
      ["Active files", overview.activeFileCount],
      ["Logical data", formatBytes(overview.logicalBytes)],
      ["Stored objects", `${overview.objectCount} · ${formatBytes(overview.objectBytes)}`],
      ["Last completed sync", formatDate(overview.lastSuccessfulSyncUtc)]
    ];
    $("overview-cards").innerHTML = cards.map(([label, value]) =>
      `<article class="stat-card"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong></article>`).join("");
  }

  function renderUsers(users) {
    $("users-table").innerHTML = users.length === 0
      ? `<tr><td colspan="4" class="empty">No users yet.</td></tr>`
      : users.map(user => `<tr>
          <td><strong>${escapeHtml(user.userName)}</strong></td>
          <td>${user.isAdmin ? '<span class="badge admin">Admin</span>' : '<span class="badge">User</span>'}</td>
          <td>${user.vaultNames.length ? user.vaultNames.map(escapeHtml).join(", ") : "—"}<small>${user.vaultCount} vault${user.vaultCount === 1 ? "" : "s"}</small></td>
          <td>${escapeHtml(formatDate(user.lastSeenUtc))}</td>
        </tr>`).join("");

    $("password-user").innerHTML = users.map(user =>
      `<option value="${escapeHtml(user.id)}">${escapeHtml(user.userName)}</option>`).join("");
  }

  function renderVaults(vaults) {
    $("vaults-table").innerHTML = vaults.length === 0
      ? `<tr><td colspan="5" class="empty">No vaults yet.</td></tr>`
      : vaults.map(vault => `<tr>
          <td><strong>${escapeHtml(vault.name)}</strong><small>${vault.memberCount} member${vault.memberCount === 1 ? "" : "s"}</small></td>
          <td>${escapeHtml(vault.ownerUserName)}</td>
          <td>${vault.fileCount}<small>${escapeHtml(formatBytes(vault.logicalBytes))}</small></td>
          <td>${vault.currentRevision}</td>
          <td>${escapeHtml(formatDate(vault.lastSuccessfulSyncUtc))}</td>
        </tr>`).join("");

    $("membership-vault").innerHTML = vaults.map(vault =>
      `<option value="${escapeHtml(vault.id)}">${escapeHtml(vault.name)}</option>`).join("");
  }

  async function refresh() {
    try {
      dashboard = await api("/api/admin/dashboard");
      renderCards(dashboard.overview);
      renderUsers(dashboard.users);
      renderVaults(dashboard.vaults);
      setMessage(`Updated ${new Date().toLocaleTimeString()}.`, "success");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to load the dashboard.", "error");
    }
  }

  $("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    setMessage("Signing in…");
    try {
      const auth = await api("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({
          userName: $("login-username").value,
          password: $("login-password").value
        })
      });
      token = auth.token;
      $("login-panel").hidden = true;
      $("dashboard").hidden = false;
      $("refresh-button").hidden = false;
      $("login-password").value = "";
      await refresh();
    } catch (error) {
      token = "";
      setMessage(error instanceof Error ? error.message : "Sign-in failed.", "error");
    }
  });

  $("refresh-button").addEventListener("click", () => void refresh());

  $("create-user-form").addEventListener("submit", async event => {
    event.preventDefault();
    try {
      await api("/api/admin/users", {
        method: "POST",
        body: JSON.stringify({
          userName: $("new-username").value,
          password: $("new-password").value,
          isAdmin: $("new-is-admin").checked
        })
      });
      event.target.reset();
      setMessage("User created.", "success");
      await refresh();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to create the user.", "error");
    }
  });

  $("reset-password-form").addEventListener("submit", async event => {
    event.preventDefault();
    try {
      await api(`/api/admin/users/${encodeURIComponent($("password-user").value)}/password`, {
        method: "POST",
        body: JSON.stringify({ password: $("reset-password").value })
      });
      event.target.reset();
      setMessage("Password updated.", "success");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to update the password.", "error");
    }
  });

  $("membership-form").addEventListener("submit", async event => {
    event.preventDefault();
    try {
      await api(`/api/admin/vaults/${encodeURIComponent($("membership-vault").value)}/members`, {
        method: "PUT",
        body: JSON.stringify({
          userName: $("membership-username").value,
          role: $("membership-role").value
        })
      });
      event.target.reset();
      setMessage("Vault access saved.", "success");
      await refresh();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Unable to save vault access.", "error");
    }
  });
})();
