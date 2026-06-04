async function getJson(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url} -> ${res.status}`);
  return await res.json();
}

function renderDrivers(drivers) {
  const feed = document.getElementById("driverFeed");
  feed.innerHTML = "";

  if (!drivers || drivers.length === 0) {
    feed.innerHTML = `<div class="item"><div class="title">No live driver pings</div></div>`;
    return;
  }

  for (const d of drivers) {
    const row = document.createElement("div");
    row.className = "item";
    row.innerHTML =
      `<div class="title">${d.driverName || d.driverId || "Driver"}</div>` +
      `<div class="meta">X: ${d.x} | Z: ${d.z} | Speed: ${d.speedMph ?? 0} mph | Heading: ${d.headingDeg ?? 0}° | Duty: ${d.dutyStatus || "-"} | Updated: ${d.serverTsUtc || "-"}</div>`;
    feed.appendChild(row);
  }
}

async function loadGuilds() {
  const data = await getJson("/api/vtc/servers");
  const sel = document.getElementById("mapGuildSelect");
  sel.innerHTML = "";

  const servers = data.servers || [];
  for (const s of servers) {
    const opt = document.createElement("option");
    opt.value = s.id;
    opt.textContent = s.name;
    sel.appendChild(opt);
  }

  if (servers.length === 0) {
    document.getElementById("mapStatus").textContent = "No guilds available.";
  }
}

async function refreshMapFeed() {
  const sel = document.getElementById("mapGuildSelect");
  const guildId = sel.value;
  if (!guildId) return;

  const data = await getJson(`/api/location/latest?guildId=${encodeURIComponent(guildId)}`);
  document.getElementById("mapStatus").textContent = `Driver feed refreshed: ${new Date().toLocaleTimeString()}`;
  renderDrivers(data.drivers || []);
}

async function boot() {
  try {
    await loadGuilds();
    await refreshMapFeed();
  } catch (err) {
    console.error(err);
    document.getElementById("mapStatus").textContent = "Failed to load live map feed.";
  }
}

document.getElementById("mapRefreshBtn").addEventListener("click", refreshMapFeed);
document.getElementById("mapGuildSelect").addEventListener("change", refreshMapFeed);

boot();
setInterval(refreshMapFeed, 15000);
