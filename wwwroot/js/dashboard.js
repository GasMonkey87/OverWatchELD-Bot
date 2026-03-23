async function getJson(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url} -> ${res.status}`);
  return await res.json();
}

function uptimeText(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  return `${h}h ${m}m ${s}s`;
}

function renderServers(servers) {
  const el = document.getElementById("serversList");
  const sel = document.getElementById("guildSelect");
  el.innerHTML = "";
  sel.innerHTML = "";

  if (!servers || servers.length === 0) {
    el.innerHTML = `<div class="item"><div class="title">No servers found</div></div>`;
    return;
  }

  for (const s of servers) {
    const row = document.createElement("div");
    row.className = "item";
    row.innerHTML = `<div class="title">${s.name}</div><div class="meta">Guild ID: ${s.id}</div>`;
    el.appendChild(row);

    const opt = document.createElement("option");
    opt.value = s.id;
    opt.textContent = s.name;
    sel.appendChild(opt);
  }
}

function renderLeaderboard(top) {
  const el = document.getElementById("leaderboardList");
  el.innerHTML = "";

  if (!top || top.length === 0) {
    el.innerHTML = `<div class="item"><div class="title">No performance data yet</div></div>`;
    return;
  }

  top.forEach((p, idx) => {
    const row = document.createElement("div");
    row.className = "item";
    row.innerHTML =
      `<div class="title">#${idx + 1} — ${p.discordUserId}</div>` +
      `<div class="meta">Score: ${Math.round(p.score ?? 0)} | Week Miles: ${Math.round(p.milesWeek ?? 0)} | Loads: ${p.loadsWeek ?? 0} | Perf: ${p.performancePct ?? 0}%</div>`;
    el.appendChild(row);
  });
}

async function refreshStatus() {
  const status = await getJson("/api/status");
  document.getElementById("statusText").textContent = status.discordReady ? "ONLINE ✅" : "STARTING ⏳";
  document.getElementById("guildCount").textContent = status.guilds ?? 0;
  document.getElementById("uptimeText").textContent = uptimeText(status.uptimeSeconds ?? 0);
  document.getElementById("buildText").textContent = status.version ?? "-";
}

async function refreshServers() {
  const data = await getJson("/api/vtc/servers");
  renderServers(data.servers || []);
}

async function refreshLeaderboard() {
  const guildId = document.getElementById("guildSelect").value;
  if (!guildId) return;
  const data = await getJson(`/api/performance/top?guildId=${encodeURIComponent(guildId)}&take=10`);
  renderLeaderboard(data.top || []);
}

async function boot() {
  try {
    await refreshStatus();
    await refreshServers();
    await refreshLeaderboard();
  } catch (err) {
    console.error(err);
  }
}

document.getElementById("refreshBtn").addEventListener("click", async () => {
  await refreshStatus();
  await refreshLeaderboard();
});

document.getElementById("guildSelect").addEventListener("change", refreshLeaderboard);

boot();
setInterval(refreshStatus, 15000);
