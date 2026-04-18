(async function(){

const status = document.getElementById("statusText");
const select = document.getElementById("serverSelect");
const btn = document.getElementById("enterVtcBtn");

function setStatus(t){ status.textContent = t; }

// STEP 1: check auth
let me;
try {
  const res = await fetch("/api/auth/me", { credentials:"include" });
  me = await res.json();
} catch {
  setStatus("Not logged in. Redirecting...");
  window.location.href = "/login";
  return;
}

// STEP 2: load VTCs
let vtcs = [];
try {
  const res = await fetch("/api/vtc/servers", { credentials:"include" });
  const data = await res.json();
  vtcs = data.data || data || [];
} catch {
  setStatus("Failed to load VTCs");
  return;
}

if(!vtcs.length){
  setStatus("No VTCs found. Make sure you're in a VTC Discord with the bot.");
  return;
}

setStatus("Select your VTC");

vtcs.forEach(v=>{
  const opt = document.createElement("option");
  opt.value = v.guildId;
  opt.textContent = v.vtcName || v.name;
  select.appendChild(opt);
});

// STEP 3: select VTC
btn.onclick = async ()=>{
  const guildId = select.value;

  if(!guildId){
    setStatus("Select a VTC first");
    return;
  }

  setStatus("Connecting...");

  await fetch("/api/auth/select-vtc", {
    method:"POST",
    headers:{ "Content-Type":"application/json" },
    credentials:"include",
    body: JSON.stringify({ guildId })
  });

  window.location.href = "/portal.html";
};

})();
