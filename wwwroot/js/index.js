(async function () {
  const pageTitle = document.getElementById("pageTitle");
  const heroTitle = document.getElementById("heroTitle");
  const welcomeText = document.getElementById("welcomeText");
  const heroBox = document.getElementById("heroBox");
  const joinDiscordBtn = document.getElementById("joinDiscordBtn");
  const reportIssueBtn = document.getElementById("reportIssueBtn");
  const learnMoreBtn = document.getElementById("learnMoreBtn");

  async function getJson(url) {
    const res = await fetch(url, { credentials: "include" });
    let data = null;
    try { data = await res.json(); } catch {}
    return { res, data };
  }

  function applySettings(s) {
    if (!s) return;

    const siteTitle = s.siteTitle || s.title || "OverWatch ELD Hub";
    const text = s.welcomeText || s.homeIntro || "";
    const joinDiscordUrl = s.joinDiscordUrl || s.discordInviteUrl || (s.discord && s.discord.joinDiscordUrl) || "#";
    const reportIssueUrl = s.reportIssueUrl || "#";
    const heroImageUrl = s.heroImageUrl || s.bannerImageUrl || "";
    const learnMoreUrl = s.learnMoreUrl || "#";

    document.title = siteTitle;
    pageTitle.textContent = siteTitle;
    heroTitle.textContent = siteTitle;
    if (text) welcomeText.textContent = text;

    joinDiscordBtn.href = joinDiscordUrl || "#";
    reportIssueBtn.href = reportIssueUrl || "#";
    learnMoreBtn.href = learnMoreUrl || "#";

    if (heroImageUrl) {
      heroBox.style.backgroundImage = `url("${heroImageUrl.replaceAll('"', "")}")`;
    }
  }

  try {
    const vtcs = await getJson("/api/auth/vtcs");

    if (vtcs.res.ok && vtcs.data?.ok && Array.isArray(vtcs.data.data) && vtcs.data.data.length === 1) {
      const guildId = vtcs.data.data[0].guildId;
      const settingsRes = await getJson(`/api/vtc/settings?guildId=${encodeURIComponent(guildId)}`);
      if (settingsRes.res.ok && settingsRes.data?.ok) {
        applySettings(settingsRes.data.data || {});
        return;
      }
    }
  } catch {}

  applySettings({});
})();
