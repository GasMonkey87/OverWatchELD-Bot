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
    throw new Error(text || "invalid json");
  }
  if (!res.ok) throw new Error(data.error || `${url} -> ${res.status}`);
  return data;
}

function qs(name) {
  return new URLSearchParams(window.location.search).get(name) || "";
}

function currentGuildId() {
  const ddl = document.getElementById("guildSelect");
  return (ddl?.value || qs("guildId") || "").trim();
}

function canManageGuild(g) {
  let perms = 0n;
  try {
    perms = BigInt(g.permissions_new || g.permissions || "0");
  } catch {
  }
  const isAdmin = (perms & 0x8n) !== 0n;
  const canManage = (perms & 0x20n) !== 0n;
  return g.owner === true || isAdmin || canManage;
}

async function loadBotServers() {
  const data = await getJson("/api/vtc/servers");
  return data.servers || [];
}

function populateGuilds(botServers, authGuilds) {
  const sel = document.getElementById("guildSelect");
  if (!sel) return;

  sel.innerHTML = "";

  const authManageableIds = new Set(
    (authGuilds || []).filter(canManageGuild).map(g => g.id)
  );

  const allowedServers = (botServers || []).filter(s => authManageableIds.has(s.id));

  for (const s of allowedServers) {
    const opt = document.createElement("option");
    opt.value = s.id;
    opt.textContent = s.name;
    sel.appendChild(opt);
  }

  const urlGuild = qs("guildId");
  if (urlGuild && allowedServers.some(s => s.id === urlGuild)) {
    sel.value = urlGuild;
  }

  if (!sel.value && sel.options.length > 0) {
    sel.selectedIndex = 0;
  }
}

function apiUrl(path) {
  const guildId = currentGuildId();
  return `${path}${path.includes("?") ? "&" : "?"}guildId=${encodeURIComponent(guildId)}`;
}

function fillLoadEditor(load) {
  document.getElementById("loadId").value = load?.id || "";
  document.getElementById("loadNumber").value = load?.loadNumber || "";
  document.getElementById("priority").value = load?.priority || "normal";
  document.getElementById("status").value = load?.status || "unassigned";
  document.getElementById("commodity").value = load?.commodity || "";
  document.getElementById("pickupLocation").value = load?.pickupLocation || "";
  document.getElementById("dropoffLocation").value = load?.dropoffLocation || "";
  document.getElementById("driverDiscordUserId").value = load?.driverDiscordUserId || "";
  document.getElementById("driverName").value = load?.driverName || "";
  document.getElementById("truckId").value = load?.truckId || "";
  document.getElementById("bolNumber").value = load?.bolNumber || "";
  document.getElementById("dispatcherNotes").value = load?.dispatcherNotes || "";
}

async function loadBoard() {
  const data = await getJson(apiUrl("/api/dispatch/board"));
  const boardCounts = document.getElementById("boardCounts");
  if (boardCounts) {
    boardCounts.textContent =
      `Unassigned: ${data.counts.unassigned} | Assigned: ${data.counts.assigned} | In Transit: ${data.counts.inTransit} | Delivered: ${data.counts.delivered} | Delayed: ${data.counts.delayed} | Stale Drivers: ${data.staleDrivers}`;
  }
}

async function loadLoads() {
  const data = await getJson(apiUrl("/api/dispatch/loads"));
  const el = document.getElementById("loadList");
  if (!el) return;

  el.innerHTML = "";

  for (const load of (data.rows || [])) {
    const div = document.createElement("div");
    div.className = "row";
    div.innerHTML = `
      <div><strong>${load.loadNumber}</strong><span class="pill">${load.status}</span></div>
      <div class="muted">${load.commodity || "-"} | ${load.pickupLocation || "-"} → ${load.dropoffLocation || "-"}</div>
      <div class="muted">Driver: ${load.driverName || "-"} | Truck: ${load.truckId || "-"}</div>`;
    div.addEventListener("click", async () => {
      fillLoadEditor(load);
      if (load.driverDiscordUserId) {
        await loadConversation(load.driverDiscordUserId);
      }
    });
    el.appendChild(div);
  }
}

async function loadConversations() {
  const data = await getJson(apiUrl("/api/dispatch/conversations"));
  const el = document.getElementById("conversationList");
  if (!el) return;

  el.innerHTML = "";

  for (const row of (data.rows || [])) {
    const div = document.createElement("div");
    div.className = "row";
    div.innerHTML = `
      <div><strong>${row.driverName || row.driverDiscordUserId}</strong>${row.unread ? `<span class="pill">${row.unread} unread</span>` : ""}</div>
      <div class="muted">${row.lastText || ""}</div>`;
    div.addEventListener("click", async () => {
      document.getElementById("driverDiscordUserId").value = row.driverDiscordUserId || "";
      document.getElementById("driverName").value = row.driverName || "";
      await loadConversation(row.driverDiscordUserId);
    });
    el.appendChild(div);
  }
}

async function loadConversation(driverDiscordUserId) {
  const data = await getJson(apiUrl(`/api/dispatch/conversations/${encodeURIComponent(driverDiscordUserId)}`));
  const el = document.getElementById("messageThread");
  if (!el) return;

  el.innerHTML = "";

  for (const m of (data.rows || [])) {
    const div = document.createElement("div");
    div.className = `msg ${m.direction === "from_driver" ? "from" : "to"}`;
    div.innerHTML = `<div>${m.text || ""}</div><div class="muted">${m.createdUtc || ""}</div>`;
    el.appendChild(div);
  }

  await getJson("/api/dispatch/messages/mark-read", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      guildId: currentGuildId(),
      driverDiscordUserId
    })
  });

  await loadConversations();
}

async function loadDrivers() {
  const data = await getJson(apiUrl("/api/dispatch/drivers/live"));
  const el = document.getElementById("driverList");
  if (!el) return;

  el.innerHTML = "";

  for (const d of (data.rows || [])) {
    const div = document.createElement("div");
    div.className = "row";
    div.innerHTML = `
      <div><strong>${d.driverName || d.discordUserId}</strong><span class="pill">${d.dutyStatus || "unknown"}</span></div>
      <div class="muted">${d.truck || "-"} | ${d.loadNumber || "-"} | ${d.location || "-"}</div>`;
    div.addEventListener("click", async () => {
      document.getElementById("driverDiscordUserId").value = d.discordUserId || "";
      document.getElementById("driverName").value = d.driverName || "";
      document.getElementById("truckId").value = d.truck || "";
      await loadConversation(d.discordUserId);
    });
    el.appendChild(div);
  }
}

function collectLoad() {
  return {
    id: document.getElementById("loadId").value.trim(),
    guildId: currentGuildId(),
    loadNumber: document.getElementById("loadNumber").value.trim(),
    priority: document.getElementById("priority").value.trim(),
    status: document.getElementById("status").value.trim(),
    commodity: document.getElementById("commodity").value.trim(),
    pickupLocation: document.getElementById("pickupLocation").value.trim(),
    dropoffLocation: document.getElementById("dropoffLocation").value.trim(),
    driverDiscordUserId: document.getElementById("driverDiscordUserId").value.trim(),
    driverName: document.getElementById("driverName").value.trim(),
    truckId: document.getElementById("truckId").value.trim(),
    bolNumber: document.getElementById("bolNumber").value.trim(),
    dispatcherNotes: document.getElementById("dispatcherNotes").value.trim()
  };
}

async function saveLoad() {
  const load = collectLoad();
  const endpoint = load.id ? "/api/dispatch/loads/update" : "/api/dispatch/loads/create";
  await getJson(`${endpoint}?guildId=${encodeURIComponent(currentGuildId())}`, {
    method: load.id ? "PUT" : "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(load)
  });
  await refreshAll();
}

async function assignLoad() {
  const load = collectLoad();
  if (!load.id) {
    alert("Save the load first.");
    return;
  }

  await getJson(`/api/dispatch/loads/assign?guildId=${encodeURIComponent(currentGuildId())}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(load)
  });

  await refreshAll();
}

async function markStatus(status) {
  const id = document.getElementById("loadId").value.trim();
  if (!id) {
    alert("Select a load first.");
    return;
  }

  await getJson(`/api/dispatch/loads/status?guildId=${encodeURIComponent(currentGuildId())}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id, status })
  });

  await refreshAll();
}

async function sendMessage() {
  const driverDiscordUserId = document.getElementById("driverDiscordUserId").value.trim();
  const driverName = document.getElementById("driverName").value.trim();
  const text = document.getElementById("messageText").value.trim();

  if (!driverDiscordUserId || !text) {
    alert("Pick a driver and type a message.");
    return;
  }

  await getJson("/api/hub/messages/send", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      guildId: currentGuildId(),
      discordUserId: driverDiscordUserId,
      driverName: driverName,
      text: text
    })
  });

  document.getElementById("messageText").value = "";
  await loadConversation(driverDiscordUserId);
}

async function refreshAll() {
  await Promise.all([
    loadBoard(),
    loadLoads(),
    loadConversations(),
    loadDrivers()
  ]);
}

window.addEventListener("DOMContentLoaded", async () => {
  const auth = await checkAuth();
  if (!auth) return;

  const botServers = await loadBotServers();
  populateGuilds(botServers, auth.guilds || []);
  await refreshAll();
});

document.getElementById("guildSelect")?.addEventListener("change", async () => {
  const u = new URL(window.location.href);
  u.searchParams.set("guildId", currentGuildId());
  window.history.replaceState({}, "", u.toString());
  await refreshAll();
});

document.getElementById("refreshBtn")?.addEventListener("click", refreshAll);
document.getElementById("newLoadBtn")?.addEventListener("click", () => fillLoadEditor(null));
document.getElementById("saveLoadBtn")?.addEventListener("click", saveLoad);
document.getElementById("assignLoadBtn")?.addEventListener("click", assignLoad);
document.getElementById("markAssignedBtn")?.addEventListener("click", () => markStatus("assigned"));
document.getElementById("markTransitBtn")?.addEventListener("click", () => markStatus("in_transit"));
document.getElementById("sendMessageBtn")?.addEventListener("click", sendMessage);

setInterval(refreshAll, 15000);
