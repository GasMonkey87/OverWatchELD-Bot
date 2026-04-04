async function checkAuth() {
  const res = await fetch("/api/auth/me");
  const data = await res.json();

  if (!data.ok) {
    window.location = "/login.html";
    return null;
  }

  return data;
}

async function getJson(url, options) {
  const res = await fetch(url, options);
  const text = await res.text();

  let data = {};
  try {
    data = text ? JSON.parse(text) : {};
  } catch {
    throw new Error(`${url} -> ${res.status} (${text || "invalid json"})`);
  }

  if (!res.ok) {
    throw new Error(`${url} -> ${res.status} (${data.error || text || "request failed"})`);
  }

  return data;
}

function qs(name) {
  return new URLSearchParams(window.location.search).get(name) || "";
}

function currentGuildId() {
  const ddl = document.getElementById("guildSelect");
  return (ddl?.value || qs("guildId") || "").trim();
}

function apiUrl(path) {
  const guildId = currentGuildId();
  if (!guildId) return path;
  return `${path}${path.includes("?") ? "&" : "?"}guildId=${encodeURIComponent(guildId)}`;
}

function uptimeText(seconds) {
  const total = Number(seconds || 0);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return `${h}h ${m}m ${s}s`;
}

function setText(id, value) {
  const el = document.getElementById(id);
  if (el) el.textContent = value ?? "-";
}

function yesNo(value) {
  return value ? "YES" : "NO";
}

function showError(message) {
  const errEl = document.getElementById("dashboardError");
  if (errEl) errEl.textContent = message || "";
}

function clearError() {
  showError("");
}

async function loadBotServers() {
  const data = await getJson("/api/vtc/servers");
  return data.servers || [];
}

function populateGuilds(servers) {
  const sel = document.getElementById("guildSelect");
  if (!sel) return;

  sel.innerHTML = "";

  for (const s of servers || []) {
    const opt = document.createElement("option");
    opt.value = s.id;
    opt.textContent = s.name;
    sel.appendChild(opt);
  }

  const urlGuild = qs("guildId");
  if (urlGuild) sel.value = urlGuild;

  if (!sel.value && sel.options.length > 0) {
    sel.selectedIndex = 0;
  }
}

function renderTopDrivers(top) {
  const el = document.getElementById("leaderboardList");
  if (!el) return;
  el.innerHTML = "";

  if (!top || top.length === 0) {
    el.innerHTML = `<div class="item"><div class="title">No performance data yet</div></div>`;
    return;
  }

  top.forEach((p, idx) => {
    const row = document.createElement("div");
    row.className = "item";
    row.innerHTML =
      `<div class="title">#${idx + 1} — ${p.name || "Driver"}</div>` +
      `<div class="meta">Score: ${Math.round(p.score ?? 0)} | Week Miles: ${Math.round(p.milesWeek ?? 0)} | Loads: ${p.loadsWeek ?? 0}</div>`;
    el.appendChild(row);
  });
}

function renderPerformance(rows) {
  const el = document.getElementById("performanceList");
  if (!el) return;
  el.innerHTML = "";

  if (!rows || rows.length === 0) {
    el.innerHTML = `<div class="item"><div class="title">No performance rows yet</div></div>`;
    return;
  }

  rows.forEach((r, idx) => {
    const row = document.createElement("div");
    row.className = "item";
    row.innerHTML =
      `<div class="title">#${idx + 1} — ${r.driverName || "Driver"}</div>` +
      `<div class="meta">Score: ${Math.round(r.score ?? 0)} | Week Miles: ${Math.round(r.milesWeek ?? 0)} | Loads: ${r.loadsWeek ?? 0}</div>`;
    el.appendChild(row);
  });
}

function renderDrivers(drivers) {
  const body = document.getElementById("driversBody");
  if (!body) return;
  body.innerHTML = "";

  if (!drivers || drivers.length === 0) {
    body.innerHTML = `<tr><td colspan="9">No drivers found</td></tr>`;
    return;
  }

  for (const d of drivers) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td>${d.name || "-"}</td>
      <td>${d.role || "driver"}</td>
      <td>${d.status || "offline"}</td>
      <td>${d.paired ? "Yes" : "No"}</td>
      <td>${d.truck || ""}</td>
      <td>${d.loadNumber || ""}</td>
      <td>${d.location || ""}</td>
      <td>${Math.round(d.score ?? 0)}</td>
      <td>${Math.round(d.weekMiles ?? 0)} / ${d.loads ?? 0}</td>`;
    body.appendChild(tr);
  }
}

async function refreshStatus() {
  const status = await getJson("/api/status");
  setText("statusText", status.discordReady ? "ONLINE ✅" : "STARTING ⏳");
  setText("guildCount", status.guilds ?? 0);
  setText("uptimeText", uptimeText(status.uptimeSeconds ?? 0));
  setText("buildText", status.version ?? "-");
}

async function loadSummary() {
  const data = await getJson(apiUrl("/api/dashboard/summary"));
  setText("vtcName", data.vtcName || "-");
  setText("driversTotal", data.driversTotal ?? 0);
  setText("driversOnline", data.driversOnline ?? 0);
  setText("pairedDrivers", data.pairedDrivers ?? 0);
  setText("dispatchReady", yesNo(data.dispatchReady));
  setText("announcementsReady", yesNo(data.announcementsReady));
  renderTopDrivers(data.topDrivers || []);
}

async function loadDrivers() {
  const data = await getJson(apiUrl("/api/dashboard/drivers"));
  renderDrivers(data.drivers || []);
}

async function loadPerformance() {
  const data = await getJson(apiUrl("/api/dashboard/performance?take=10"));
  renderPerformance(data.rows || []);
}

async function loadSettings() {
  const data = await getJson(apiUrl("/api/dashboard/settings"));
  const s = data.settings || {};

  const dispatchChannelId = document.getElementById("dispatchChannelId");
  const dispatchWebhookUrl = document.getElementById("dispatchWebhookUrl");
  const announcementChannelId = document.getElementById("announcementChannelId");
  const announcementWebhookUrl = document.getElementById("announcementWebhookUrl");

  if (dispatchChannelId) dispatchChannelId.value = s.dispatchChannelId || "";
  if (dispatchWebhookUrl) dispatchWebhookUrl.value = s.dispatchWebhookUrl || "";
  if (announcementChannelId) announcementChannelId.value = s.announcementChannelId || "";
  if (announcementWebhookUrl) announcementWebhookUrl.value = s.announcementWebhookUrl || "";
}

async function saveSettings() {
  const payload = {
    guildId: currentGuildId(),
    dispatchChannelId: document.getElementById("dispatchChannelId")?.value.trim() || "",
    dispatchWebhookUrl: document.getElementById("dispatchWebhookUrl")?.value.trim() || "",
    announcementChannelId: document.getElementById("announcementChannelId")?.value.trim() || "",
    announcementWebhookUrl: document.getElementById("announcementWebhookUrl")?.value.trim() || ""
  };

  const data = await getJson("/api/dashboard/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });

  if (!data.ok) throw new Error(data.error || "Failed to save settings");
  await loadSummary();
  await loadSettings();
  alert("Settings saved.");
}

async function loadDashboard() {
  clearError();

  if (!currentGuildId()) {
    showError("No Discord server selected.");
    return;
  }

  const errors = [];

  await Promise.all([
    loadSummary().catch(err => errors.push(`Summary: ${err.message || err}`)),
    loadDrivers().catch(err => errors.push(`Drivers: ${err.message || err}`)),
    loadPerformance().catch(err => errors.push(`Performance: ${err.message || err}`)),
    loadSettings().catch(err => errors.push(`Settings: ${err.message || err}`))
  ]);

  if (errors.length > 0) {
    showError(errors.join(" | "));
  }
}

window.addEventListener("DOMContentLoaded", async () => {
  try {
    const auth = await checkAuth();
    if (!auth) return;

    await refreshStatus();

    const servers = await loadBotServers();
    populateGuilds(servers);

    await loadDashboard();
  } catch (err) {
    console.error(err);
    showError(`Dashboard error: ${err.message || err}`);
  }
});

document.getElementById("refreshBtn")?.addEventListener("click", async () => {
  await refreshStatus();
  await loadDashboard();
});

document.getElementById("saveSettingsBtn")?.addEventListener("click", async () => {
  try {
    await saveSettings();
  } catch (err) {
    alert(`Save failed: ${err.message || err}`);
  }
});

document.getElementById("guildSelect")?.addEventListener("change", async () => {
  const u = new URL(window.location.href);
  if (currentGuildId()) u.searchParams.set("guildId", currentGuildId());
  window.history.replaceState({}, "", u.toString());
  await loadDashboard();
});

setInterval(refreshStatus, 15000);
