(function () {
  const state = {
    me: null,
    servers: [],
    selectedGuildId: "",
    pairing: null,
  };

  const els = {
    serverSelect: document.getElementById("serverSelect"),
    pairCode: document.getElementById("pairCode"),
    connectDiscordBtn: document.getElementById("connectDiscordBtn"),
    enterBtn: document.getElementById("enterVtcBtn"),
    statusText: document.getElementById("statusText"),
    helpText: document.getElementById("helpText"),
    userChip: document.getElementById("userChip"),
  };

  function setStatus(message, isError) {
    if (!els.statusText) return;
    els.statusText.textContent = message || "";
    els.statusText.style.color = isError ? "#ffb4b4" : "#dfe9f8";
  }

  function setHelp(message) {
    if (!els.helpText) return;
    els.helpText.textContent = message || "";
  }

  function getQueryParam(name) {
    const url = new URL(window.location.href);
    return url.searchParams.get(name) || "";
  }

  function readStoredJson(key) {
    try {
      const raw = localStorage.getItem(key);
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  }

  function writeStoredJson(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch {
      // ignore storage errors
    }
  }

  async function fetchJson(url, options) {
    const response = await fetch(url, {
      credentials: "include",
      headers: {
        "Accept": "application/json",
        ...(options && options.body ? { "Content-Type": "application/json" } : {}),
      },
      ...options,
    });

    let data = null;
    try {
      data = await response.json();
    } catch {
      data = null;
    }

    if (!response.ok) {
      const error = (data && (data.error || data.message || data.title)) || `HTTP ${response.status}`;
      throw new Error(error);
    }

    return data;
  }

  function renderUser() {
    if (!els.userChip) return;

    if (state.me && (state.me.username || state.me.name)) {
      const username = state.me.username || state.me.name;
      els.userChip.textContent = `Connected as ${username}`;
      els.userChip.style.display = "inline-flex";
      return;
    }

    els.userChip.style.display = "none";
  }

  function renderServers() {
    if (!els.serverSelect) return;

    const currentValue = state.selectedGuildId || "";
    els.serverSelect.innerHTML = "";

    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = state.servers.length
      ? "Select your VTC..."
      : "Login with Discord to load your VTCs";
    els.serverSelect.appendChild(placeholder);

    for (const server of state.servers) {
      const opt = document.createElement("option");
      opt.value = server.guildId || server.id || "";
      opt.textContent = server.vtcName || server.name || server.guildName || "Unnamed VTC";
      els.serverSelect.appendChild(opt);
    }

    if (currentValue) {
      els.serverSelect.value = currentValue;
    }
  }

  async function loadMe() {
    try {
      const me = await fetchJson("/api/auth/me");
      state.me = me && (me.data || me.user || me);
      renderUser();
      return true;
    } catch {
      state.me = null;
      renderUser();
      return false;
    }
  }

  async function loadServers() {
    setStatus("Loading available VTCs...", false);

    const result = await fetchJson("/api/vtc/servers");
    const items = (result && (result.data || result.servers || result.items)) || [];

    state.servers = Array.isArray(items) ? items : [];
    renderServers();

    if (!state.servers.length) {
      setStatus("No VTCs were found for this Discord account yet.", true);
      setHelp("Join the correct VTC Discord server first, then come back and refresh this page.");
      return;
    }

    setStatus("Select your VTC and enter your pairing code.", false);
    setHelp("Your pairing code is generated from the bot inside your VTC Discord server.");
  }

  function findSelectedServer() {
    const guildId = els.serverSelect ? els.serverSelect.value : "";
    return state.servers.find(x => (x.guildId || x.id || "") === guildId) || null;
  }

  async function claimPairing() {
    const code = (els.pairCode && els.pairCode.value || "").trim();
    const selectedServer = findSelectedServer();

    if (!state.me) {
      setStatus("Please connect Discord first.", true);
      return;
    }

    if (!selectedServer) {
      setStatus("Please select your VTC.", true);
      return;
    }

    if (!code) {
      setStatus("Please enter your pairing code.", true);
      return;
    }

    setStatus("Linking your Discord account to the selected VTC...", false);

    const url = `/api/vtc/pair/claim?code=${encodeURIComponent(code)}`;
    const result = await fetchJson(url, { method: "GET" });
    const pairing = result && (result.data || result);

    const returnedGuildId = pairing && (pairing.guildId || pairing.discordGuildId || "");
    const selectedGuildId = selectedServer.guildId || selectedServer.id || "";

    if (returnedGuildId && selectedGuildId && returnedGuildId !== selectedGuildId) {
      throw new Error("That pairing code belongs to a different VTC. Please choose the matching VTC or generate a new code.");
    }

    state.pairing = pairing;

    writeStoredJson("oweld.connectedVtc", {
      guildId: pairing.guildId || selectedGuildId,
      vtcName: pairing.vtcName || selectedServer.vtcName || selectedServer.name || "",
      discordUserId: pairing.discordUserId || state.me.id || "",
      discordUsername: pairing.discordUsername || state.me.username || "",
      linkedAtUtc: new Date().toISOString(),
    });

    setStatus("VTC connected successfully. Redirecting to your driver portal...", false);

    const portalUrl = new URL("/portal.html", window.location.origin);
    portalUrl.searchParams.set("guildId", pairing.guildId || selectedGuildId);
    if (pairing.vtcName || selectedServer.vtcName || selectedServer.name) {
      portalUrl.searchParams.set("vtcName", pairing.vtcName || selectedServer.vtcName || selectedServer.name);
    }

    window.location.href = portalUrl.toString();
  }

  function handleDiscordLogin() {
    const returnUrl = `${window.location.origin}/connect-vtc.html`;
    window.location.href = `/auth/discord/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  async function boot() {
    renderServers();

    if (els.connectDiscordBtn) {
      els.connectDiscordBtn.addEventListener("click", handleDiscordLogin);
    }

    if (els.enterBtn) {
      els.enterBtn.addEventListener("click", async function () {
        try {
          await claimPairing();
        } catch (error) {
          setStatus(error && error.message ? error.message : "Unable to connect to the selected VTC.", true);
        }
      });
    }

    if (els.serverSelect) {
      els.serverSelect.addEventListener("change", function () {
        state.selectedGuildId = els.serverSelect.value || "";
      });
    }

    const success = getQueryParam("auth") || getQueryParam("connected");
    if (success) {
      setStatus("Discord connected. Loading your available VTCs...", false);
    } else {
      setStatus("Connect Discord to load your available VTCs.", false);
    }

    const isLoggedIn = await loadMe();
    if (!isLoggedIn) {
      setHelp("Use the Connect Discord button to sign in first.");
      return;
    }

    try {
      await loadServers();
    } catch (error) {
      setStatus(error && error.message ? error.message : "Could not load VTC list.", true);
      setHelp("Check that your Discord login is valid and your account belongs to a VTC server using the bot.");
    }
  }

  boot();
})();
