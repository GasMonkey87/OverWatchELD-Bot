(async function () {
  const qs = new URLSearchParams(window.location.search);
  const guildId = (qs.get("guildId") || "").trim();

  const userPill = document.getElementById("userPill");
  const heroTitle = document.getElementById("heroTitle");
  const guildIdChip = document.getElementById("guildIdChip");
  const userNameChip = document.getElementById("userNameChip");
  const refreshBtn = document.getElementById("refreshBtn");
  const logoutBtn = document.getElementById("logoutBtn");
  const openDriverPortalLink = document.getElementById("openDriverPortalLink");

  const driverCountValue = document.getElementById("driverCountValue");
  const liveCountValue = document.getElementById("liveCountValue");
  const messageCountValue = document.getElementById("messageCountValue");
  const attentionCountValue = document.getElementById("attentionCountValue");
  const mapPointValue = document.getElementById("mapPointValue");
  const guildValue = document.getElementById("guildValue");

  const rosterList = document.getElementById("rosterList");
  const driverSearch = document.getElementById("driverSearch");
  const selectedDriverCard = document.getElementById("selectedDriverCard");

  const profileDriverName = document.getElementById("profileDriverName");
  const profileDiscordUserId = document.getElementById("profileDiscordUserId");
  const profileRole = document.getElementById("profileRole");
  const profileLoadNumber = document.getElementById("profileLoadNumber");
  const profileNotes = document.getElementById("profileNotes");
  const driverProfileForm = document.getElementById("driverProfileForm");
  const profileStatus = document.getElementById("profileStatus");

  const conversationList = document.getElementById("conversationList");

  const dispatchForm = document.getElementById("dispatchForm");
  const dispatchDriverName = document.getElementById("dispatchDriverName");
  const dispatchLoadNumber = document.getElementById("dispatchLoadNumber");
  const dispatchMessage = document.getElementById("dispatchMessage");
  const clearDispatchBtn = document.getElementById("clearDispatchBtn");
  const dispatchStatus = document.getElementById("dispatchStatus");
  const sendDispatchBtn = document.getElementById("sendDispatchBtn");

  const fleetMessageForm = document.getElementById("fleetMessageForm");
  const fleetMessageText = document.getElementById("fleetMessageText");
  const fleetMessageStatus = document.getElementById("fleetMessageStatus");

  const loadForm = document.getElementById("loadForm");
  const loadNumberInput = document.getElementById("loadNumberInput");
  const loadCommodityInput = document.getElementById("loadCommodityInput");
  const loadPriorityInput = document.getElementById("loadPriorityInput");
  const pickupInput = document.getElementById("pickupInput");
  const dropoffInput = document.getElementById("dropoffInput");
  const assignDriverInput = document.getElementById("assignDriverInput");
  const loadTruckInput = document.getElementById("loadTruckInput");
  const loadNotesInput = document.getElementById("loadNotesInput");
  const loadStatus = document.getElementById("loadStatus");

  const announcementForm = document.getElementById("announcementForm");
  const announcementTitle = document.getElementById("announcementTitle");
  const announcementBody = document.getElementById("announcementBody");
  const clearAnnouncementBtn = document.getElementById("clearAnnouncementBtn");
  const announcementStatus = document.getElementById("announcementStatus");
  const announcementList = document.getElementById("announcementList");

  const homepageSettingsForm = document.getElementById("homepageSettingsForm");
  const siteTitleInput = document.getElementById("siteTitleInput");
  const welcomeTextInput = document.getElementById("welcomeTextInput");
  const joinDiscordUrlInput = document.getElementById("joinDiscordUrlInput");
  const reportIssueUrlInput = document.getElementById("reportIssueUrlInput");
  const heroImageUrlInput = document.getElementById("heroImageUrlInput");
  const learnMoreUrlInput = document.getElementById("learnMoreUrlInput");

  const companyInfoInput = document.getElementById("companyInfoInput");
  const latestInfoInput = document.getElementById("latestInfoInput");
  const featuredDriversInput = document.getElementById("featuredDriversInput");
  const homepageSettingsStatus = document.getElementById("homepageSettingsStatus");

  const discordAutoSetupForm = document.getElementById("discordAutoSetupForm");
  const autoCategoryName = document.getElementById("autoCategoryName");
  const autoDispatchChannelName = document.getElementById("autoDispatchChannelName");
  const autoAnnouncementsChannelName = document.getElementById("autoAnnouncementsChannelName");
  const autoBolChannelName = document.getElementById("autoBolChannelName");
  const autoSystemLogChannelName = document.getElementById("autoSystemLogChannelName");
  const autoSetupStatus = document.getElementById("autoSetupStatus");
  const autoSetupResult = document.getElementById("autoSetupResult");

  let me = null;
  let managementDrivers = [];
  let selectedDriver = null;
  let dispatchLoads = [];

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


  function formatJsonField(value, fallback = "") {
    if (value == null || value === "") return fallback;
    if (typeof value === "string") return value;
    try { return JSON.stringify(value, null, 2); } catch { return fallback; }
  }

  function parseJsonField(value, fallback) {
    const raw = String(value ?? "").trim();
    if (!raw) return fallback;
    try {
      const parsed = JSON.parse(raw);
      return parsed;
    } catch {
      return fallback;
    }
  }

  async function getJson(url, options) {
    const res = await fetch(url, { credentials: "include", ...options });
    let data = null;
    try { data = await res.json(); } catch {}
    return { res, data };
  }

  async function postJson(url, body, method = "POST") {
    const res = await fetch(url, {
      method,
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });

    let data = null;
    try { data = await res.json(); } catch {}
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
    guildValue.textContent = guildId || "-";
    heroTitle.textContent = `Management Dashboard - ${displayName}`;

    if (openDriverPortalLink) {
      openDriverPortalLink.href = `/portal.html?guildId=${encodeURIComponent(guildId)}`;
    }

    return true;
  }

  async function loadSummary() {
    const { res, data } = await getJson(`/api/management/summary?guildId=${encodeURIComponent(guildId)}`);
    if (!res.ok || !data?.ok) return;

    const s = data.data || {};
    driverCountValue.textContent = String(s.drivers || 0);
    liveCountValue.textContent = String(s.live || 0);
    messageCountValue.textContent = String(s.unreadDispatch || 0);
    attentionCountValue.textContent = String(s.needsAttention || 0);
  }

  async function loadDrivers() {
    const { res, data } = await getJson(`/api/management/drivers?guildId=${encodeURIComponent(guildId)}`);
    if (!res.ok || !data?.ok) {
      rosterList.innerHTML = `<div class="muted">Failed to load drivers.</div>`;
      return;
    }

    managementDrivers = Array.isArray(data.data) ? data.data : [];
    renderRoster();
    populateDriverSelect();

    if (!selectedDriver && managementDrivers.length) {
      await selectDriver(managementDrivers[0].discordUserId);
    }
  }

  function populateDriverSelect() {
    const current = assignDriverInput ? assignDriverInput.value : "";
    if (!assignDriverInput) return;

    assignDriverInput.innerHTML = `<option value="">Unassigned</option>` +
      managementDrivers.map(d => {
        const id = d.discordUserId || "";
        const name = d.driverName || d.name || d.discordUsername || id;
        return `<option value="${escapeHtml(id)}">${escapeHtml(name)}</option>`;
      }).join("");
    assignDriverInput.value = current;
  }

  function renderRoster() {
    const term = (driverSearch?.value || "").trim().toLowerCase();

    const rows = managementDrivers.filter(row => {
      if (!term) return true;
      const hay = [
        row.driverName, row.name, row.discordUsername, row.role,
        row.truckNumber, row.truck, row.loadNumber, row.location,
        row.dutyStatus, row.status, row.discordUserId
      ].join(" ").toLowerCase();
      return hay.includes(term);
    });

    if (!rows.length) {
      rosterList.innerHTML = `<div class="muted">No matching drivers found.</div>`;
      return;
    }

    rosterList.innerHTML = rows.map(row => {
      const id = row.discordUserId || "";
      const name = row.driverName || row.name || row.discordUsername || id;
      const active = selectedDriver && String(selectedDriver.discordUserId || "") === String(id);

      return `
        <div class="roster-item ${active ? "active" : ""}" data-user-id="${escapeHtml(id)}">
          <strong>${escapeHtml(name)}</strong>
          <div class="muted">Role: ${escapeHtml(fmt(row.role, "Driver"))}</div>
          <div class="muted">Duty: ${escapeHtml(fmt(row.dutyStatus || row.status))}</div>
          <div class="muted">Truck: ${escapeHtml(fmt(row.truckNumber || row.truck))}</div>
          <div class="muted">Load: ${escapeHtml(fmt(row.loadNumber))}</div>
          <div class="muted">Unread: ${escapeHtml(String(row.unreadDispatchCount || 0))}</div>
        </div>
      `;
    }).join("");

    rosterList.querySelectorAll(".roster-item").forEach(el => {
      el.addEventListener("click", async () => {
        const userId = el.getAttribute("data-user-id") || "";
        await selectDriver(userId);
      });
    });
  }

  async function selectDriver(discordUserId) {
    if (!discordUserId) return;

    const { res, data } = await getJson(
      `/api/management/drivers/${encodeURIComponent(discordUserId)}?guildId=${encodeURIComponent(guildId)}`
    );

    if (!res.ok || !data?.ok) {
      selectedDriver = null;
      renderSelectedDriver();
      return;
    }

    selectedDriver = data.data?.driver || null;
    renderSelectedDriver();
    await loadConversation();
    renderRoster();
  }

  function renderSelectedDriver() {
    if (!selectedDriver) {
      if (selectedDriverCard) selectedDriverCard.innerHTML = `<div class="muted">Select a driver from the roster.</div>`;
      if (profileDriverName) profileDriverName.value = "";
      if (profileDiscordUserId) profileDiscordUserId.value = "";
      if (profileRole) profileRole.value = "Driver";
      if (profileLoadNumber) profileLoadNumber.value = "";
      if (profileNotes) profileNotes.value = "";
      if (dispatchDriverName) dispatchDriverName.value = "";
      if (dispatchLoadNumber) dispatchLoadNumber.value = "";
      return;
    }

    const name = selectedDriver.driverName || selectedDriver.name || selectedDriver.discordUsername || selectedDriver.discordUserId || "Driver";

    if (selectedDriverCard) {
      selectedDriverCard.innerHTML = `
        <div class="list-item">
          <strong>${escapeHtml(name)}</strong>
          <div class="muted">Discord User ID: ${escapeHtml(fmt(selectedDriver.discordUserId))}</div>
          <div class="muted">Role: ${escapeHtml(fmt(selectedDriver.role, "Driver"))}</div>
          <div class="muted">Duty Status: ${escapeHtml(fmt(selectedDriver.dutyStatus || selectedDriver.status))}</div>
          <div class="muted">Truck: ${escapeHtml(fmt(selectedDriver.truckNumber || selectedDriver.truck))}</div>
          <div class="muted">Load Number: ${escapeHtml(fmt(selectedDriver.loadNumber))}</div>
          <div class="muted">Location: ${escapeHtml(fmt(selectedDriver.location))}</div>
          <div class="muted">Unread Dispatch: ${escapeHtml(String(selectedDriver.unreadDispatchCount || 0))}</div>
        </div>
      `;
    }

    if (profileDriverName) profileDriverName.value = name;
    if (profileDiscordUserId) profileDiscordUserId.value = selectedDriver.discordUserId || "";
    if (profileRole) profileRole.value = selectedDriver.role || "Driver";
    if (profileLoadNumber) profileLoadNumber.value = selectedDriver.loadNumber || "";
    if (profileNotes) profileNotes.value = selectedDriver.notes || "";

    if (dispatchDriverName) dispatchDriverName.value = name;
    if (dispatchLoadNumber) dispatchLoadNumber.value = selectedDriver.loadNumber || "";
  }

  async function loadConversation() {
    if (!conversationList) return;

    if (!selectedDriver?.discordUserId) {
      conversationList.innerHTML = `<div class="muted">Select a driver to load dispatch conversation.</div>`;
      return;
    }

    const { res, data } = await getJson(
      `/api/hub/messages/thread?guildId=${encodeURIComponent(guildId)}&discordUserId=${encodeURIComponent(selectedDriver.discordUserId)}`
    );

    if (!res.ok || !data?.ok) {
      conversationList.innerHTML = `<div class="muted">Could not load conversation.</div>`;
      return;
    }

    const rows = Array.isArray(data.rows) ? data.rows : [];
    if (!rows.length) {
      conversationList.innerHTML = `<div class="muted">No messages found for this driver yet.</div>`;
      return;
    }

    conversationList.innerHTML = rows.slice().reverse().map(row => {
      const direction = String(row.direction || "").toLowerCase();
      const title =
        direction === "from_driver" ? "Driver" :
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

  async function saveDriverProfile() {
    if (!profileStatus) return;

    profileStatus.textContent = "";

    if (!selectedDriver?.discordUserId) {
      profileStatus.textContent = "Select a driver first.";
      return;
    }

    const roleResult = await postJson("/api/management/driver/role", {
      guildId,
      driverDiscordUserId: selectedDriver.discordUserId,
      role: profileRole.value
    });

    if (!roleResult.res.ok || !roleResult.data?.ok) {
      profileStatus.textContent = roleResult.data?.error || "Failed to save role.";
      return;
    }

    const noteResult = await postJson("/api/management/driver/note", {
      guildId,
      driverDiscordUserId: selectedDriver.discordUserId,
      notes: profileNotes.value || ""
    });

    if (!noteResult.res.ok || !noteResult.data?.ok) {
      profileStatus.textContent = noteResult.data?.error || "Role saved, but note save failed.";
      return;
    }

    selectedDriver.role = profileRole.value;
    selectedDriver.notes = profileNotes.value || "";
    selectedDriver.loadNumber = profileLoadNumber.value || selectedDriver.loadNumber || "";

    profileStatus.textContent = "Driver profile saved.";
    await loadDrivers();
    await selectDriver(selectedDriver.discordUserId);
  }

  async function sendDispatchMessage() {
    if (!dispatchStatus) return;

    dispatchStatus.textContent = "";

    if (!selectedDriver?.discordUserId) {
      dispatchStatus.textContent = "Select a driver first.";
      return;
    }

    const text = (dispatchMessage?.value || "").trim();
    if (!text) {
      dispatchStatus.textContent = "Enter a message first.";
      return;
    }

    if (sendDispatchBtn) sendDispatchBtn.disabled = true;
    dispatchStatus.textContent = "Sending message...";

    try {
      const { res, data } = await postJson(`/api/dispatch/messages/send?guildId=${encodeURIComponent(guildId)}`, {
        driverDiscordUserId: selectedDriver.discordUserId,
        driverName: dispatchDriverName.value || "Driver",
        text,
        loadNumber: (dispatchLoadNumber.value || "").trim()
      });

      if (!res.ok || !data?.ok) {
        dispatchStatus.textContent = data?.error || "Failed to send message.";
        return;
      }

      dispatchMessage.value = "";
      dispatchStatus.textContent = "Dispatch message sent.";
      await loadConversation();
      await loadSummary();
    } finally {
      if (sendDispatchBtn) sendDispatchBtn.disabled = false;
    }
  }

    async function sendFleetMessage() {
    if (!fleetMessageStatus) return;

    fleetMessageStatus.textContent = "";
    const text = (fleetMessageText?.value || "").trim();
    if (!text) {
      fleetMessageStatus.textContent = "Enter a message first.";
      return;
    }

    fleetMessageStatus.textContent = "Sending fleet message...";

    try {
      const res = await fetch("/api/management/message/fleet", {
        method: "POST",
        credentials: "include",
        headers: {
          "Content-Type": "application/json",
          "X-Requested-With": "fetch"
        },
        body: JSON.stringify({
          guildId,
          text
        })
      });

      let data = null;
      try { data = await res.json(); } catch {}

      if (!res.ok || !data?.ok) {
        fleetMessageStatus.textContent =
          (data?.error || `Failed to send fleet message. HTTP ${res.status}`);
        console.error("fleet send failed", res.status, data);
        return;
      }

      fleetMessageText.value = "";
      fleetMessageStatus.textContent =
        `Fleet message sent to ${data.sent ?? 0} drivers.`;
      await loadSummary();
    } catch (err) {
      console.error(err);
      fleetMessageStatus.textContent = "Fleet message request failed.";
    }
  }

  async function loadAnnouncements() {
    if (!announcementList) return;

    const { res, data } = await getJson(`/api/announcements?guildId=${encodeURIComponent(guildId)}`);
    if (!res.ok || !data?.ok) {
      announcementList.innerHTML = `<div class="muted">Could not load announcements.</div>`;
      return;
    }

    const rows = Array.isArray(data.data) ? data.data : [];
    if (!rows.length) {
      announcementList.innerHTML = `<div class="muted">No announcements yet.</div>`;
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

  async function postAnnouncement() {
    if (!announcementStatus) return;

    announcementStatus.textContent = "";

    const title = (announcementTitle?.value || "").trim();
    const body = (announcementBody?.value || "").trim();

    if (!title && !body) {
      announcementStatus.textContent = "Enter a title or body first.";
      return;
    }

    announcementStatus.textContent = "Posting...";

    const { res, data } = await postJson("/api/announcements/create", {
      guildId,
      title,
      body
    });

    if (!res.ok || !data?.ok) {
      announcementStatus.textContent = data?.error || "Failed to post announcement.";
      return;
    }

    announcementTitle.value = "";
    announcementBody.value = "";
    announcementStatus.textContent = "Announcement posted.";
    await loadAnnouncements();
  }

  async function loadHomepageSettings() {
    const { res, data } = await getJson(`/api/vtc/settings?guildId=${encodeURIComponent(guildId)}`);
    if (!res.ok || !data?.ok) {
      if (homepageSettingsStatus) homepageSettingsStatus.textContent = "Could not load homepage settings.";
      return;
    }

    const s = data.data || {};
    const discord = s.discord || {};

    if (siteTitleInput) siteTitleInput.value = s.siteTitle || s.title || "";
    if (welcomeTextInput) welcomeTextInput.value = s.welcomeText || s.homeIntro || "";
    if (joinDiscordUrlInput) joinDiscordUrlInput.value = s.joinDiscordUrl || s.discordInviteUrl || discord.joinDiscordUrl || "";
    if (reportIssueUrlInput) reportIssueUrlInput.value = s.reportIssueUrl || "";
    if (heroImageUrlInput) heroImageUrlInput.value = s.heroImageUrl || s.bannerImageUrl || "";
    if (learnMoreUrlInput) learnMoreUrlInput.value = s.learnMoreUrl || "";
    if (companyInfoInput) companyInfoInput.value = s.companyInfo || s.aboutText || "";
    if (latestInfoInput) latestInfoInput.value = formatJsonField(s.latestInfo, "");
    if (featuredDriversInput) featuredDriversInput.value = formatJsonField(s.featuredDrivers, "");
  }

  async function saveHomepageSettings() {
    if (!homepageSettingsStatus) return;

    homepageSettingsStatus.textContent = "Saving...";

    const payload = {
      guildId,
      siteTitle: siteTitleInput?.value || "",
      welcomeText: welcomeTextInput?.value || "",
      joinDiscordUrl: joinDiscordUrlInput?.value || "",
      reportIssueUrl: reportIssueUrlInput?.value || "",
      heroImageUrl: heroImageUrlInput?.value || "",
      learnMoreUrl: learnMoreUrlInput?.value || "",
      companyInfo: companyInfoInput?.value || "",
      latestInfo: parseJsonField(latestInfoInput?.value, []),
      featuredDrivers: parseJsonField(featuredDriversInput?.value, []),
      discord: {
        joinDiscordUrl: joinDiscordUrlInput?.value || ""
      }
    };

    const { res, data } = await postJson("/api/vtc/settings/update", payload);

    if (!res.ok || !data?.ok) {
      homepageSettingsStatus.textContent = data?.error || "Failed to save homepage settings.";
      return;
    }

    homepageSettingsStatus.textContent = "Homepage settings saved.";
  }

  async function autoSetupDiscord() {
    if (!autoSetupStatus || !autoSetupResult) return;

    autoSetupStatus.textContent = "Creating channels and webhooks...";
    autoSetupResult.innerHTML = "";

    const payload = {
      guildId,
      categoryName: autoCategoryName?.value || "OverWatch ELD",
      dispatchChannelName: autoDispatchChannelName?.value || "dispatch-center",
      announcementsChannelName: autoAnnouncementsChannelName?.value || "announcements",
      bolChannelName: autoBolChannelName?.value || "bol-documents",
      systemLogChannelName: autoSystemLogChannelName?.value || "system-logs"
    };

    const { res, data } = await postJson("/api/vtc/setup/auto-discord", payload);

    if (!res.ok || !data?.ok) {
      autoSetupStatus.textContent = data?.error || "Auto setup failed.";
      return;
    }

    autoSetupStatus.textContent = "Discord auto setup completed.";

    const ch = data.channels || {};
    autoSetupResult.innerHTML = `
      <div class="list-item">
        <strong>Category</strong>
        <div class="muted">${escapeHtml((data.category && data.category.name) || "")}</div>
      </div>
      <div class="list-item">
        <strong>Dispatch</strong>
        <div class="muted">Channel ID: ${escapeHtml(ch.dispatch?.id || "")}</div>
        <div class="muted">Webhook: ${escapeHtml(ch.dispatch?.webhookUrl || "")}</div>
      </div>
      <div class="list-item">
        <strong>Announcements</strong>
        <div class="muted">Channel ID: ${escapeHtml(ch.announcements?.id || "")}</div>
        <div class="muted">Webhook: ${escapeHtml(ch.announcements?.webhookUrl || "")}</div>
      </div>
      <div class="list-item">
        <strong>BOL</strong>
        <div class="muted">Channel ID: ${escapeHtml(ch.bol?.id || "")}</div>
      </div>
      <div class="list-item">
        <strong>System Logs</strong>
        <div class="muted">Channel ID: ${escapeHtml(ch.systemLogs?.id || "")}</div>
      </div>
    `;

    await loadHomepageSettings();
  }

  async function loadDispatchBoard() {
    const [boardRes, loadsRes, mapRes] = await Promise.all([
      getJson(`/api/dispatch/board?guildId=${encodeURIComponent(guildId)}`),
      getJson(`/api/dispatch/loads?guildId=${encodeURIComponent(guildId)}`),
      getJson(`/api/map/live?guildId=${encodeURIComponent(guildId)}`)
    ]);

    if (boardRes.res.ok && boardRes.data?.ok) {
      const counts = boardRes.data.counts || {};
      messageCountValue.textContent = String(counts.unreadMessages || 0);
    }

    if (mapRes.res.ok && mapRes.data?.ok) {
      const points = Array.isArray(mapRes.data.points) ? mapRes.data.points : [];
      mapPointValue.textContent = String(points.length);
    }

    if (!loadsRes.res.ok || !loadsRes.data?.ok) {
      renderBoard([]);
      return;
    }

    dispatchLoads = Array.isArray(loadsRes.data.rows) ? loadsRes.data.rows : [];
    renderBoard(dispatchLoads);
  }

  function renderBoard(loads) {
    const statuses = ["unassigned", "assigned", "in_transit", "delivered"];
    for (const status of statuses) {
      const container = document.getElementById(`board-${status}`);
      if (!container) continue;

      const rows = loads.filter(x => normalizeStatus(x.status) === status);
      if (!rows.length) {
        container.innerHTML = `<div class="muted">No loads</div>`;
        continue;
      }

      container.innerHTML = rows.map(load => {
        const id = load.id || "";
        const number = load.loadNumber || "Load";
        const driver = load.driverName || "Unassigned";
        const commodity = load.commodity || "-";
        const pickup = load.pickupLocation || "-";
        const dropoff = load.dropoffLocation || "-";
        const priority = load.priority || "normal";

        return `
          <div class="load-card">
            <strong>${escapeHtml(number)}</strong>
            <div class="muted">${escapeHtml(commodity)}</div>
            <div class="muted">Driver: ${escapeHtml(driver)}</div>
            <div class="muted">From: ${escapeHtml(pickup)}</div>
            <div class="muted">To: ${escapeHtml(dropoff)}</div>
            <span class="tag">${escapeHtml(priority)}</span>
            <span class="tag">${escapeHtml(normalizeStatus(load.status))}</span>
            <div class="toolbar" style="margin-top:10px;">
              <button class="btn btn-ghost load-status-btn" data-id="${escapeHtml(id)}" data-status="assigned" type="button">Assigned</button>
              <button class="btn btn-ghost load-status-btn" data-id="${escapeHtml(id)}" data-status="in_transit" type="button">Transit</button>
              <button class="btn btn-ghost load-status-btn" data-id="${escapeHtml(id)}" data-status="delivered" type="button">Delivered</button>
            </div>
          </div>
        `;
      }).join("");
    }

    document.querySelectorAll(".load-status-btn").forEach(btn => {
      btn.addEventListener("click", async () => {
        const id = btn.getAttribute("data-id") || "";
        const status = btn.getAttribute("data-status") || "";
        await updateLoadStatus(id, status);
      });
    });
  }

  function normalizeStatus(status) {
    const s = String(status || "").trim().toLowerCase();
    if (s === "picked_up" || s === "pickedup") return "in_transit";
    if (s === "delayed") return "assigned";
    return s || "unassigned";
  }

  async function createLoad() {
    if (!loadStatus) return;

    loadStatus.textContent = "Creating load...";

    const selectedDriverId = assignDriverInput?.value || "";
    const selectedDriverObj = managementDrivers.find(x => String(x.discordUserId || "") === String(selectedDriverId));
    const selectedDriverName = selectedDriverObj
      ? (selectedDriverObj.driverName || selectedDriverObj.name || selectedDriverObj.discordUsername || selectedDriverId)
      : "";

    const payload = {
      guildId,
      loadNumber: loadNumberInput?.value || "",
      commodity: loadCommodityInput?.value || "",
      priority: loadPriorityInput?.value || "normal",
      pickupLocation: pickupInput?.value || "",
      dropoffLocation: dropoffInput?.value || "",
      driverDiscordUserId: selectedDriverId,
      driverName: selectedDriverName,
      truckId: loadTruckInput?.value || "",
      dispatcherNotes: loadNotesInput?.value || ""
    };

    const { res, data } = await postJson(`/api/dispatch/loads/create?guildId=${encodeURIComponent(guildId)}`, payload);
    if (!res.ok || !data?.ok) {
      loadStatus.textContent = data?.error || "Failed to create load.";
      return;
    }

    if (loadForm) loadForm.reset();
    populateDriverSelect();
    loadStatus.textContent = "Load created.";
    await loadDispatchBoard();
  }

  async function updateLoadStatus(id, status) {
    if (!id || !status || !loadStatus) return;

    loadStatus.textContent = `Updating load to ${status}...`;

    const { res, data } = await postJson(`/api/dispatch/loads/status?guildId=${encodeURIComponent(guildId)}`, {
      id,
      status
    });

    if (!res.ok || !data?.ok) {
      loadStatus.textContent = data?.error || "Failed to update load status.";
      return;
    }

    loadStatus.textContent = "Load status updated.";
    await loadDispatchBoard();
    await loadSummary();
  }

  async function logout() {
    try {
      await fetch("/logout", { method: "POST", credentials: "include" });
    } catch {}
    window.location.href = "/index.html";
  }

  async function refreshAll() {
    refreshBtn.disabled = true;
    refreshBtn.textContent = "Refreshing...";
    try {
      const ok = await loadMe();
      if (!ok) return;

      await Promise.all([
        loadSummary(),
        loadDrivers(),
        loadAnnouncements(),
        loadHomepageSettings(),
        loadDispatchBoard()
      ]);

      if (selectedDriver?.discordUserId) {
        await loadConversation();
      }
    } finally {
      refreshBtn.disabled = false;
      refreshBtn.textContent = "Refresh";
    }
  }

  if (driverSearch) driverSearch.addEventListener("input", renderRoster);

  if (driverProfileForm) {
    driverProfileForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      await saveDriverProfile();
    });
  }

  if (dispatchForm) {
    dispatchForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      await sendDispatchMessage();
    });
  }

  if (clearDispatchBtn) {
    clearDispatchBtn.addEventListener("click", () => {
      if (dispatchMessage) dispatchMessage.value = "";
      if (dispatchStatus) dispatchStatus.textContent = "";
    });
  }

    if (fleetMessageForm) {
    fleetMessageForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      e.stopPropagation();
      await sendFleetMessage();
      return false;
    });
  }

  if (loadForm) {
    loadForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      await createLoad();
    });
  }

  if (announcementForm) {
    announcementForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      await postAnnouncement();
    });
  }

  if (clearAnnouncementBtn) {
    clearAnnouncementBtn.addEventListener("click", () => {
      if (announcementTitle) announcementTitle.value = "";
      if (announcementBody) announcementBody.value = "";
      if (announcementStatus) announcementStatus.textContent = "";
    });
  }

  if (homepageSettingsForm) {
    homepageSettingsForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      await saveHomepageSettings();
    });
  }

  if (discordAutoSetupForm) {
    discordAutoSetupForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      await autoSetupDiscord();
    });
  }

  refreshBtn.addEventListener("click", refreshAll);
  logoutBtn.addEventListener("click", logout);

  await refreshAll();
})();
