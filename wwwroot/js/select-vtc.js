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
    enterBtn: document.getElementById("enterVtcBtn"),
    statusText: document.getElementById("statusText"),
    helpText: document.getElementById("helpText"),
    userChip: document.getElementById("userChip"),
  };

  function setStatus(message, isError) {
    if (!els.statusText) return;
    els.statusText.textContent = message || "";
    els.statusText.style.color = isError ? "#ffb4b4" : "#d7ffeb";
  }

  function setHelp(message) {
    if (!els.helpText) return;
    els.helpText.textContent = message || "";
  }

  function getQueryParam(name) {
    const url = new URL(window.location.href);
    return url.searchParams.get(name) || "";
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
        Accept: "application/json",
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
      const error = (data && (data.error || data.message || data.title || data.detail)) || `HTTP ${response.status}`;
      throw new Error(error);
    }

    return data;
  }

  function normalizeArray(result) {
    if (!result) return [];
    if (Array.isArray(result)) return result;
    if (Array.isArray(result.data)) return result.data;
    if (Array.isArray(result.items)) return result.items;
    if (Array.isArray(result.servers)) return result.servers;
    if (Array.isArray(result.guilds)) return result.guilds;
    if (Array.isArray(result.results)) return result.results;
    return [];
  }

  function normalizeUser(result) {
    if (!result) return null;
    if (result.data && typeof result.data === "object") return result.data;
    if (result.user && typeof result.user === "object") return result.user;
    return result;
  }

  function normalizeServer(server) {
    const guildId = server.guildId || server.id || server.discordGuildId || server.serverId || "";
    const vtcName = server.vtcName || server.name || server.guildName || server.serverName || "Unnamed VTC";
    return {
      raw: server,
      guildId,
      vtcName,
    };
  }

  function renderUser() {
    if (!els.userChip) return;

    if (state.me && (state.me.username || state.me.global_name || state.me.name)) {
      const username = state.me.username || state.me.global_name || state.me.name;
      els.userChip.innerHTML = '<span class="status-dot"></span> Connected as ' + username;
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
      : "No VTCs found for this account";
    els.serverSelect.appendChild(placeholder);

    for (const server of state.servers) {
      const opt = document.createElement("option");
      opt.value = server.guildId || "";
      opt.textContent = server.vtcName || "Unnamed VTC";
      els.serverSelect.appendChild(opt);
    }

    if (currentValue) {
      els.serverSelect.value = currentValue;
    }
  }

  async function tryAuthEndpoints() {
    const endpoints = [
      "/api/auth/me",
      "/api/discord/me",
      "/auth/me",
      "/api/me"
    ];

    for (const endpoint of endpoints) {
      try {
        const result = await fetchJson(endpoint);
        const user = normalizeUser(result);
        if (user) {
          state.me = user;
          return true;
        }
      } catch {
        // try next
      }
    }

    state.me = null;
    return false;
  }

  async function tryServerEndpoints() {
    const endpoints = [
      "/api/vtc/servers",
      "/api/discord/servers",
      "/api/discord/guilds",
      "/api/vtc/guilds"
    ];

    for (const endpoint of endpoints) {
      try {
        const result = await fetchJson(endpoint);
        const items = normalizeArray(result).map(normalizeServer).filter(x => x.guildId || x.vtcName);
        if (items.length) {
          state.servers = items;
          return { endpoint, count: items.length };
        }
      } catch {
        // try next
      }
    }

    state.servers = [];
    return null;
  }

  function findSelectedServer() {
    const guildId = els.serverSelect ? els.serverSelect.value : "";
    return state.servers.find(x => x.guildId === guildId) || null;
  }

  async function claimPairing() {
    const code = ((els.pairCode && els.pairCode.value) || "").trim();
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

    const result = await fetchJson(`/api/vtc/pair/claim?code=${encodeURIComponent(code)}`, { method: "GET" });
    const pairing = normalizeUser(result) || result;

    const returnedGuildId = pairing.guildId || pairing.discordGuildId || "";
    const selectedGuildId = selectedServer.guildId || "";

    if (returnedGuildId && selectedGuildId && returnedGuildId !== selectedGuildId) {
      throw new Error("That pairing code belongs to a different VTC. Please choose the matching VTC or generate a new code.");
    }

    state.pairing = pairing;

    writeStoredJson("oweld.connectedVtc", {
      guildId: pairing.guildId || selectedGuildId,
      vtcName: pairing.vtcName || selectedServer.vtcName || "",
      discordUserId: pairing.discordUserId || state.me.id || "",
      discordUsername: pairing.discordUsername || state.me.username || state.me.name || "",
      linkedAtUtc: new Date().toISOString(),
    });

    setStatus("VTC connected successfully. Redirecting to your driver portal...", false);

    const portalUrl = new URL("/portal.html", window.location.origin);
    portalUrl.searchParams.set("guildId", pairing.guildId || selectedGuildId);
    if (pairing.vtcName || selectedServer.vtcName) {
      portalUrl.searchParams.set("vtcName", pairing.vtcName || selectedServer.vtcName);
    }

    window.location.href = portalUrl.toString();
  }

  function handleDiscordLogin() {
    const returnUrl = `${window.location.origin}/connect-vtc.html?auth=1`;
    window.location.href = `/auth/discord/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  async function boot() {
    renderServers();

    document.querySelectorAll("#connectDiscordBtn").forEach(function (btn) {
      btn.addEventListener("click", function (e) {
        e.preventDefault();
        handleDiscordLogin();
      });
    });

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

    if (getQueryParam("auth") || getQueryParam("connected")) {
      setStatus("Discord connected. Loading your available VTCs...", false);
    } else {
      setStatus("Connect Discord to load your available VTCs.", false);
    }

    const isLoggedIn = await tryAuthEndpoints();
    renderUser();

    if (!isLoggedIn) {
      setHelp("You authenticated, but the site could not read your logged-in Discord session. Check your auth callback and cookie/session settings.");
      setStatus("Discord login was not detected on this page.", true);
      return;
    }

    try {
      const loaded = await tryServerEndpoints();
      renderServers();

      if (!loaded || !state.servers.length) {
        setStatus("No VTCs were found for this Discord account.", true);
        setHelp("Most likely causes: the endpoint is returning an empty list, the site is hitting the wrong endpoint name, or this Discord account is not in a bot-enabled VTC yet.");
        return;
      }

      setStatus(`Loaded ${loaded.count} VTC${loaded.count === 1 ? "" : "s"}. Select one and enter your pairing code.`, false);
      setHelp("If your VTC is missing, make sure the bot is in that Discord server and you are logged in with the same Discord account used in the VTC.");
    } catch (error) {
      renderServers();
      setStatus(error && error.message ? error.message : "Could not load VTC list.", true);
      setHelp("Check that your Discord login is valid and your server list endpoint is returning JSON.");
    }
  }

  boot();
})();
