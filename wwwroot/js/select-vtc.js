(function () {
  const els = {
    serverSelect: document.getElementById("serverSelect"),
    pairCode: document.getElementById("pairCode"),
    enterBtn: document.getElementById("enterVtcBtn"),
    statusText: document.getElementById("statusText"),
    helpText: document.getElementById("helpText"),
    userChip: document.getElementById("userChip"),
  };

  const state = {
    me: null,
    vtcs: [],
    selectedGuildId: ""
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

  async function fetchJson(url, options) {
    const res = await fetch(url, {
      credentials: "include",
      headers: {
        "Accept": "application/json",
        ...(options && options.body ? { "Content-Type": "application/json" } : {})
      },
      ...options
    });

    let data = null;
    try {
      data = await res.json();
    } catch {
      data = null;
    }

    if (!res.ok) {
      const msg =
        (data && (data.error || data.message || data.title || data.detail)) ||
        `HTTP ${res.status}`;
      throw new Error(msg);
    }

    return data;
  }

  function renderUser() {
    if (!els.userChip) return;

    if (state.me && (state.me.username || state.me.globalName || state.me.global_name)) {
      const name = state.me.username || state.me.globalName || state.me.global_name;
      els.userChip.innerHTML = '<span class="status-dot"></span> Connected as ' + name;
      els.userChip.style.display = "inline-flex";
    } else {
      els.userChip.style.display = "none";
    }
  }

  function renderVtcs() {
    if (!els.serverSelect) return;

    els.serverSelect.innerHTML = "";

    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = state.vtcs.length
      ? "Select your VTC..."
      : "No VTCs found for this account";
    els.serverSelect.appendChild(placeholder);

    for (const v of state.vtcs) {
      const opt = document.createElement("option");
      opt.value = v.guildId || "";
      opt.textContent = v.vtcName || "Unnamed VTC";
      els.serverSelect.appendChild(opt);
    }

    if (state.selectedGuildId) {
      els.serverSelect.value = state.selectedGuildId;
    }
  }

  async function loadMe() {
    const result = await fetchJson("/api/auth/me");
    if (!result?.ok || !result?.data) {
      throw new Error("NotAuthenticated");
    }

    state.me = result.data;
    renderUser();
  }

  async function loadVtcs() {
    const result = await fetchJson("/api/auth/vtcs");

    const items = Array.isArray(result?.data) ? result.data : [];

    state.vtcs = items.map(x => ({
      guildId: x.guildId || "",
      vtcName: x.vtcName || x.name || "Unnamed VTC",
      logoUrl: x.logoUrl || "",
      role: x.role || "Driver",
      isManager: !!x.isManager
    }));

    renderVtcs();
  }

  async function enterSelectedVtc() {
    const guildId = els.serverSelect?.value || "";

    if (!guildId) {
      setStatus("Please select your VTC.", true);
      return;
    }

    setStatus("Opening your VTC...", false);

    const result = await fetchJson("/api/auth/select-vtc", {
      method: "POST",
      body: JSON.stringify({ guildId })
    });

    if (!result?.ok) {
      throw new Error(result?.error || "SelectVtcFailed");
    }

    window.location.href = result.redirectUrl || "/portal.html";
  }

  function goLogin() {
    window.location.href = "/login";
  }

  async function boot() {
    renderVtcs();

    document.querySelectorAll("#connectDiscordBtn").forEach(btn => {
      btn.addEventListener("click", function (e) {
        e.preventDefault();
        goLogin();
      });
    });

    if (els.serverSelect) {
      els.serverSelect.addEventListener("change", function () {
        state.selectedGuildId = els.serverSelect.value || "";
      });
    }

    if (els.enterBtn) {
      els.enterBtn.addEventListener("click", async function () {
        try {
          await enterSelectedVtc();
        } catch (error) {
          setStatus(error?.message || "Unable to open your VTC.", true);
        }
      });
    }

    setStatus("Loading your Discord session...", false);

    try {
      await loadMe();
    } catch {
      setStatus("You are not logged in yet. Click Login with Discord.", true);
      setHelp("This page only works after Discord sign-in.");
      return;
    }

    try {
      await loadVtcs();

      if (!state.vtcs.length) {
        setStatus("No VTCs were found for this Discord account.", true);
        setHelp("Your login worked, but no matched VTCs were returned.");
        return;
      }

      setStatus(`Loaded ${state.vtcs.length} VTC${state.vtcs.length === 1 ? "" : "s"}. Select one to continue.`, false);
      setHelp("Choose your VTC from the dropdown and continue.");
    } catch (error) {
      setStatus(error?.message || "Could not load VTC list.", true);
      setHelp("The page could not read /api/auth/vtcs.");
    }
  }

  boot();
})();
