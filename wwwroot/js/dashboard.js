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
  if (!res.ok) throw new Error(`${url} -> ${res.status}`);
  return await res.json();
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

// 🔥 NEW: Only manager/admin guilds
function populateGuilds(guilds) {
  const sel = document.getElementById("guildSelect");
  if (!sel) return;

  sel.innerHTML = "";

  for (const g of guilds) {
    const perms = parseInt(g.permissions || "0", 10);

    const isManager =
      (perms & 0x8) !== 0 ||     // ADMIN
      (perms & 0x20) !== 0;      // MANAGE_GUILD

    if (!isManager) continue;

    const opt = document.createElement("option");
    opt.value = g.id;
    opt.textContent = g.name;
    sel.appendChild(opt);
  }

  const urlGuild = qs("guildId");
  if (urlGuild) sel.value = urlGuild;

  if (!sel.value && sel.options.length > 0)
    sel.selectedIndex = 0;
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
  setText("uptimeText", uptimeText(status.uptime ?? 0));
  setText("buildText", status.version ?? "-");
}

async function loadSummary() {
  const data = await getJson(apiUrl("/api/dashboard/summary"));
  setText("vtcName", data.vtcName || "-");
  setText("driversTotal", data.driversTotal ?? 0);
  setText("driversOnline", data.driversOnline ?? 0);
  setText("pairedDrivers", data.pairedDrivers ?? 0);
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
  document.getElementById("dispatchChannelId").value = s.dispatchChannelId || "";
}

async function loadDashboard() {
  await Promise.all([
    loadSummary(),
    loadDrivers(),
    loadPerformance(),
    loadSettings()
  ]);
}

// 🚀 NEW BOOT (uses Discord login)
window.addEventListener("DOMContentLoaded", async () => {
  try {
    const auth = await checkAuth();
    if (!auth) return;

    populateGuilds(auth.guilds);
    await refreshStatus();
    await loadDashboard();
  } catch (err) {
    console.error(err);
  }
});

document.getElementById("guildSelect")?.addEventListener("change", async () => {
  const u = new URL(window.location.href);
  u.searchParams.set("guildId", currentGuildId());
  window.history.replaceState({}, "", u.toString());
  await loadDashboard();
});
