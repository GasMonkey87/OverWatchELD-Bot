(async function () {
  const list = document.getElementById("vtcList");
  const statusBox = document.getElementById("statusBox");

  function showStatus(text) {
    if (statusBox) statusBox.textContent = text;
  }

  function escapeHtml(value) {
    return String(value || "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#39;");
  }

  try {
    const res = await fetch("/api/auth/vtcs", { credentials: "include" });
    const payload = await res.json();

    if (!res.ok || !payload?.ok) {
      showStatus(payload?.error || "Unable to load your VTC list.");
      return;
    }

    const vtcs = Array.isArray(payload.data) ? payload.data : [];

    if (vtcs.length === 0) {
      showStatus("No VTCs were found for your Discord account.");
      return;
    }

    if (vtcs.length === 1) {
      await selectVtc(vtcs[0].guildId);
      return;
    }

    showStatus("Choose a VTC to continue.");

    list.innerHTML = vtcs.map(vtc => `
      <div class="vtc-item">
        <div class="vtc-meta">
          <img class="vtc-logo" src="${escapeHtml(vtc.logoUrl || "/img/default-vtc.png")}" alt="">
          <div>
            <h3 class="vtc-name">${escapeHtml(vtc.vtcName || "Unknown VTC")}</h3>
            <p class="vtc-role">Role: ${escapeHtml(vtc.role || "Driver")}</p>
          </div>
        </div>
        <button class="btn btn-primary" data-guild-id="${escapeHtml(vtc.guildId)}">Enter</button>
      </div>
    `).join("");

    list.querySelectorAll("button[data-guild-id]").forEach(btn => {
      btn.addEventListener("click", async () => {
        const guildId = btn.getAttribute("data-guild-id");
        await selectVtc(guildId);
      });
    });

  } catch (err) {
    console.error(err);
    showStatus("An error occurred while loading your VTC options.");
  }

  async function selectVtc(guildId) {
    showStatus("Opening your VTC...");

    const res = await fetch("/api/auth/select-vtc", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "include",
      body: JSON.stringify({ guildId })
    });

    const payload = await res.json();

    if (!res.ok || !payload?.ok) {
      showStatus(payload?.error || "Unable to open that VTC.");
      return;
    }

    const redirectUrl = payload.redirectUrl || "/";
    window.location.href = redirectUrl;
  }
})();
