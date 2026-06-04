(async function () {
  const qs = new URLSearchParams(window.location.search);
  const guildId = (qs.get("guildId") || "").trim();

  const userPill = document.getElementById("userPill");
  const heroTitle = document.getElementById("heroTitle");
  const guildIdChip = document.getElementById("guildIdChip");
  const userNameChip = document.getElementById("userNameChip");
  const refreshBtn = document.getElementById("refreshBtn");
  const logoutBtn = document.getElementById("logoutBtn");

  const dutyStatusValue = document.getElementById("dutyStatusValue");
  const truckValue = document.getElementById("truckValue");
  const loadValue = document.getElementById("loadValue");
  const speedValue = document.getElementById("speedValue");
  const unreadValue = document.getElementById("unreadValue");

  const driverStatusCard = document.getElementById("driverStatusCard");
  const activeLoadCard = document.getElementById("activeLoadCard");
  const awardsList = document.getElementById("awardsList");
  const conversationList = document.getElementById("conversationList");
  const announcementList = document.getElementById("announcementList");
  const mapCard = document.getElementById("mapCard");

  let me = null;
  let myDriverStatus = null;

  function escapeHtml(v) {
    return String(v ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#39;");
  }

  function fmt(v, fallback = "-") {
    const s = String(v ?? "").trim();
    return s || fallback;
  }

  async function getJson(url, options) {
    const res = await fetch(url, {
      credentials: "include",
      ...options
    });

    let data = null;
    try {
      data = await res.json();
    } catch {
    }

    return { res, data };
  }

  async function loadMe() {
    const { res, data } = await getJson("/api/auth/me");
    if (!res.ok || !data?.ok) {
      window.location.href = "/index.html";
      return false;
    }

    me = data.data;
    const displayName = me.globalName || me.username || "User";

    userPill.textContent = `Logged in as ${displayName}`;
    userNameChip.textContent = displayName;
    guildIdChip.textContent = guildId || "-";
    heroTitle.textContent = `Welcome, ${displayName}`;

    return true;
  }

  async function loadDriverStatus() {
    if (!guildId) {
      driverStatusCard.innerHTML = `<div class="empty">Missing guildId in URL.</div>`;
      return;
    }

    const { res, data } = await getJson(`/api/eld/driver/status?guildId=${encodeURIComponent(guildId)}`);
    if (!res.ok || !data?.ok) {
      driverStatusCard.innerHTML = `<div class="empty">Could not load driver status.</div>`;
      return;
    }

    const rows = Array.isArray(data.rows) ? data.rows : [];
    const myId = String(me?.discordUserId || "");
    myDriverStatus =
      rows.find(x => String(x.discordUserId || "") === myId) ||
      rows.find(x => String(x.driverName || "").toLowerCase() === String(me?.username || "").toLowerCase()) ||
      null;

    if (!myDriverStatus) {
      driverStatusCard.innerHTML = `<div class="empty">No live driver status found yet for your account.</div>`;
      dutyStatusValue.textContent = "-";
      truckValue.textContent = "-";
      loadValue.textContent = "-";
      speedValue.textContent = "-";
      return;
    }

    dutyStatusValue.textContent = myDriverStatus.dutyStatus || "-";
    truckValue.textContent = myDriverStatus.truck || "-";
    loadValue.textContent = myDriverStatus.loadNumber || "-";
    speedValue.textContent = myDriverStatus.speedMph ? `${Number(myDriverStatus.speedMph).toFixed(0)} mph` : "-";

    driverStatusCard.innerHTML = `
      <div class="list-item">
        <strong>${escapeHtml(fmt(myDriverStatus.driverName, me.username || "Driver"))}</strong>
        <div class="muted">Duty Status: ${escapeHtml(fmt(myDriverStatus.dutyStatus))}</div>
        <div class="muted">Truck: ${escapeHtml(fmt(myDriverStatus.truck))}</div>
        <div class="muted">Load Number: ${escapeHtml(fmt(myDriverStatus.loadNumber))}</div>
        <div class="muted">Location: ${escapeHtml(fmt(myDriverStatus.location))}</div>
        <div class="muted">Speed: ${myDriverStatus.speedMph ? escapeHtml(Number(myDriverStatus.speedMph).toFixed(0)) + " mph" : "-"}</div>
        <div class="muted">Last Seen: ${escapeHtml(fmt(myDriverStatus.lastSeenUtc))}</div>
      </div>
    `;

    renderActiveLoad();
  }

  function renderActiveLoad() {
    if (!myDriverStatus) {
      activeLoadCard.innerHTML = `<div class="empty">No active load found.</div>`;
      return;
    }

    if (!myDriverStatus.loadNumber || !String(myDriverStatus.loadNumber).trim()) {
      activeLoadCard.innerHTML = `<div class="empty">No active load assigned right now.</div>`;
      return;
    }

    activeLoadCard.innerHTML = `
      <div class="list-item">
        <strong>Load ${escapeHtml(fmt(myDriverStatus.loadNumber))}</strong>
        <div class="muted">Truck: ${escapeHtml(fmt(myDriverStatus.truck))}</div>
        <div class="muted">Duty: ${escapeHtml(fmt(myDriverStatus.dutyStatus))}</div>
        <div class="muted">Location: ${escapeHtml(fmt(myDriverStatus.location))}</div>
      </div>
    `;
  }

  async function loadConversation() {
    if (!guildId || !me?.discordUserId) {
      conversationList.innerHTML = `<div class="empty">Missing session or guild information.</div>`;
      return;
    }

    const { res, data } = await getJson(
      `/api/hub/messages/thread?guildId=${encodeURIComponent(guildId)}&discordUserId=${encodeURIComponent(me.discordUserId)}`
    );

    if (!res.ok || !data?.ok) {
      conversationList.innerHTML = `<div class="empty">Could not load dispatch conversation.</div>`;
      unreadValue.textContent = "0";
      return;
    }

    const rows = Array.isArray(data.rows) ? data.rows : [];
    const unread = rows.filter(x => {
      const dir = String(x.direction || "").toLowerCase();
      return (dir === "to_driver" || dir === "outbound" || dir === "dispatch") && !x.isRead;
    }).length;

    unreadValue.textContent = String(unread);

    if (!rows.length) {
      conversationList.innerHTML = `<div class="empty">No dispatch messages found yet.</div>`;
      return;
    }

    conversationList.innerHTML = rows.slice().reverse().map(row => {
      const direction = String(row.direction || "").toLowerCase();
      const title =
        direction === "from_driver" ? "You" :
        direction === "to_driver" || direction === "outbound" ? "Dispatch" :
        direction === "system" ? "System" : "Message";

      return `
        <div class="list-item">
          <strong>${escapeHtml(title)}</strong>
          <div>${escapeHtml(row.text || "")}</div>
          <div class="muted" style="margin-top:6px;">
            ${row.loadNumber ? `Load: ${escapeHtml(row.loadNumber)} • ` : ""}
            ${escapeHtml(fmt(row.createdUtc, "Recent"))}
          </div>
        </div>
      `;
    }).join("");
  }

  async function loadAnnouncements() {
    const { res, data } = await getJson(`/api/announcements?guildId=${encodeURIComponent(guildId)}`);

    if (!res.ok || !data?.ok) {
      announcementList.innerHTML = `<div class="empty">Could not load announcements.</div>`;
      return;
    }

    const rows = Array.isArray(data.data) ? data.data : [];
    if (!rows.length) {
      announcementList.innerHTML = `<div class="empty">No announcements yet.</div>`;
      return;
    }

    announcementList.innerHTML = rows.slice(0, 8).map(a => `
      <div class="list-item">
        <strong>${escapeHtml(a.title || "Announcement")}</strong>
        <div>${escapeHtml(a.body || "")}</div>
        <div class="muted" style="margin-top:6px;">${escapeHtml(a.sentUtc || a.createdUtc || "")}</div>
      </div>
    `).join("");
  }

  async function loadMapSummary() {
    const { res, data } = await getJson(`/api/map/live?guildId=${encodeURIComponent(guildId)}`);

    if (!res.ok || !data?.ok) {
      mapCard.innerHTML = `<div class="empty">Could not load map summary.</div>`;
      return;
    }

    const points = Array.isArray(data.points) ? data.points : [];
    const myId = String(me?.discordUserId || "");
    const mine = points.find(x => String(x.discordUserId || "") === myId) || null;

    if (!mine) {
      mapCard.innerHTML = `<div class="empty">No map point available for your truck yet.</div>`;
      return;
    }

    mapCard.innerHTML = `
      <div class="list-item">
        <strong>${escapeHtml(fmt(mine.driverName, "Driver"))}</strong>
        <div class="muted">Location: ${escapeHtml(fmt(mine.location))}</div>
        <div class="muted">Latitude: ${escapeHtml(fmt(mine.latitude, "0"))}</div>
        <div class="muted">Longitude: ${escapeHtml(fmt(mine.longitude, "0"))}</div>
        <div class="muted">Heading: ${escapeHtml(fmt(mine.heading, "0"))}</div>
        <div class="muted">Speed: ${mine.speedMph ? escapeHtml(Number(mine.speedMph).toFixed(0)) + " mph" : "-"}</div>
      </div>
    `;
  }

  async function loadAwards() {
    if (!guildId || !me?.discordUserId) {
      awardsList.innerHTML = `<div class="empty">Missing session or guild information.</div>`;
      return;
    }

    const { res, data } = await getJson(
      `/api/vtc/awards/driver?guildId=${encodeURIComponent(guildId)}&driverId=${encodeURIComponent(me.discordUserId)}`
    );

    if (!res.ok || !data?.ok) {
      awardsList.innerHTML = `<div class="empty">No awards available yet.</div>`;
      return;
    }

    const rows = Array.isArray(data.awards) ? data.awards : [];
    if (!rows.length) {
      awardsList.innerHTML = `<div class="empty">No awards assigned yet.</div>`;
      return;
    }

    awardsList.innerHTML = rows.slice(0, 8).map(row => {
      const award = row.award || {};
      return `
        <div class="list-item">
          <strong>${escapeHtml(award.iconEmoji || "🏆")} ${escapeHtml(award.name || "Award")}</strong>
          <div>${escapeHtml(award.description || row.note || "")}</div>
          <div class="muted" style="margin-top:6px;">
            Awarded: ${escapeHtml(fmt(row.awardedUtc, ""))}
            ${row.awardedByUsername ? ` • By: ${escapeHtml(row.awardedByUsername)}` : ""}
          </div>
        </div>
      `;
    }).join("");
  }

  async function logout() {
    try {
      await fetch("/logout", {
        method: "POST",
        credentials: "include"
      });
    } catch {
    }
    window.location.href = "/index.html";
  }

  async function refreshAll() {
    refreshBtn.disabled = true;
    refreshBtn.textContent = "Refreshing...";
    try {
      const ok = await loadMe();
      if (!ok) return;

      await Promise.all([
        loadDriverStatus(),
        loadConversation(),
        loadAnnouncements(),
        loadMapSummary(),
        loadAwards()
      ]);
    } finally {
      refreshBtn.disabled = false;
      refreshBtn.textContent = "Refresh";
    }
  }

  refreshBtn.addEventListener("click", refreshAll);
  logoutBtn.addEventListener("click", logout);

  await refreshAll();
})();
