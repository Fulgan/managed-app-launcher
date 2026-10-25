// Shared property inspector plumbing. The Stream Deck application calls
// connectElgatoStreamDeckSocket once the page loads; fields carry data-key and
// are written back to the action's settings on change. Empty fields are
// removed from settings so the entry uses the built-in default.

let socket = null;
let context = null;
let settings = {};
let onInfo = () => {};

function connectElgatoStreamDeckSocket(port, uuid, registerEvent, info, actionInfo) {
  context = uuid;
  settings = JSON.parse(actionInfo).payload.settings || {};
  socket = new WebSocket(`ws://127.0.0.1:${port}`);
  socket.onopen = () => {
    socket.send(JSON.stringify({ event: registerEvent, uuid }));
    send("sendToPlugin", { request: "info" });
  };
  socket.onmessage = (msg) => {
    const data = JSON.parse(msg.data);
    if (data.event === "didReceiveSettings") {
      settings = data.payload.settings || {};
      fillFields();
    } else if (data.event === "sendToPropertyInspector") {
      onInfo(data.payload);
    }
  };
  fillFields();
}

function send(event, payload) {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ event, context, payload }));
  }
}

function fields() {
  return Array.from(document.querySelectorAll("[data-key]"));
}

function fillFields() {
  for (const field of fields()) {
    const value = settings[field.dataset.key];
    field.value = value === undefined || value === null ? "" : String(value);
  }
}

function readField(field) {
  const raw = field.value.trim();
  if (raw === "") return undefined;
  switch (field.dataset.type) {
    case "int": {
      const n = parseInt(raw, 10);
      return Number.isNaN(n) ? undefined : n;
    }
    case "bool":
      return raw === "true";
    default:
      return raw;
  }
}

function saveSettings() {
  const next = { ...settings };
  for (const field of fields()) {
    const value = readField(field);
    if (value === undefined) delete next[field.dataset.key];
    else next[field.dataset.key] = value;
  }
  settings = next;
  send("setSettings", settings);
}

// The Stream Deck file picker reports "C:\fakepath\name" URI-encoded; the
// application substitutes the real path after the prefix.
function pickedPath(input) {
  return decodeURIComponent(input.value.replace(/^C:\\fakepath\\/, ""));
}

function bindFilePicker(buttonId, inputId, targetId) {
  const picker = document.getElementById(inputId);
  document.getElementById(buttonId).addEventListener("click", () => picker.click());
  picker.addEventListener("change", () => {
    if (!picker.value) return;
    document.getElementById(targetId).value = pickedPath(picker);
    saveSettings();
  });
}

function fillPresets(select, presets) {
  const current = select.value;
  const keep = Array.from(select.options).filter((o) => o.dataset.fixed !== undefined);
  select.replaceChildren(...keep);
  for (const name of presets || []) {
    if (keep.some((o) => o.value === name)) continue;
    const option = document.createElement("option");
    option.value = name;
    option.textContent = name;
    select.appendChild(option);
  }
  select.value = settings[select.dataset.key] || current;
}

function describe(info) {
  const lines = [];
  if (info.resolved) {
    if (info.resolved.error) lines.push(`<span class="error">${escapeHtml(info.resolved.error)}</span>`);
    else lines.push(`${escapeHtml(info.resolved.kind)} → <code>${escapeHtml(info.resolved.launch || "")}</code>`);
    if (info.resolved.match && info.resolved.match !== info.resolved.launch) {
      lines.push(`tracks <code>${escapeHtml(info.resolved.match)}</code>`);
    }
  }
  lines.push(`config: <code>${escapeHtml(info.configPath)}</code>`);
  return lines.join("<br>");
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

document.addEventListener("change", (e) => {
  if (e.target.matches("[data-key]")) saveSettings();
});

function bindManagerButton(buttonId) {
  document.getElementById(buttonId).addEventListener("click", () => send("sendToPlugin", { request: "openManager" }));
}
