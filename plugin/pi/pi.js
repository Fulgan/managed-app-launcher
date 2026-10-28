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

// The Stream Deck file picker reports "C:\fakepath\name" URI-encoded with
// forward slashes; the application substitutes the real path after the prefix.
function pickedPath(input) {
  return decodeURIComponent(input.value.replace(/^C:\\fakepath\\/, "")).replaceAll("/", "\\");
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

const ENTRY_FIELDS = ["target", "kind", "title", "trackPath", "icon"];
const SOURCE_NAMES = { start: "Start menu", steam: "Steam library", running: "running apps" };
let picker = null;
let pickedApps = [];
let pendingSource = null;

function bindAppPicker(ids) {
  picker = Object.fromEntries(Object.entries(ids).map(([k, id]) => [k, document.getElementById(id)]));
  picker.source.addEventListener("change", () => {
    const source = picker.source.value;
    if (!source) return;
    picker.source.value = "";
    pickedApps = [];
    pendingSource = source;
    picker.filter.value = "";
    picker.list.replaceChildren();
    showLoading(`Loading ${SOURCE_NAMES[source] || source}…`);
    picker.panel.hidden = false;
    send("sendToPlugin", { request: "listApps", source });
  });
  picker.filter.addEventListener("input", renderApps);
  picker.close.addEventListener("click", closePicker);
  picker.list.addEventListener("change", () => {
    const app = pickedApps[picker.list.value];
    if (app) applyEntry(app.entry);
    closePicker();
  });
}

function showLoading(text) {
  picker.loadingText.textContent = text;
  picker.loading.hidden = !text;
  picker.list.hidden = !!text;
  picker.filter.disabled = !!text;
  picker.status.textContent = "";
}

function closePicker() {
  pendingSource = null;
  picker.panel.hidden = true;
}

function showApps(payload) {
  if (payload.source !== pendingSource) return;
  pendingSource = null;
  pickedApps = payload.apps;
  showLoading("");
  picker.status.textContent = payload.error ? payload.error : pickedApps.length ? `${pickedApps.length} apps` : "nothing found";
  renderApps();
  picker.filter.focus();
}

function renderApps() {
  const needle = picker.filter.value.trim().toLowerCase();
  const options = [];
  pickedApps.forEach((app, index) => {
    if (needle && !`${app.name} ${app.detail}`.toLowerCase().includes(needle)) return;
    const option = document.createElement("option");
    option.value = index;
    option.textContent = app.name;
    option.title = app.detail;
    options.push(option);
  });
  picker.list.replaceChildren(...options);
}

let iconPicker = null;
let iconSeq = 0;
let iconFile = null;

function bindIconPicker() {
  const el = (id) => document.getElementById(id);
  iconPicker = { panel: el("iconPanel"), grid: el("iconGrid"), loading: el("iconLoading"), status: el("iconStatus"), source: el("iconSource"), app: el("iconApp") };
  el("iconOpen").addEventListener("click", () => {
    if (iconPicker.panel.hidden) requestIcons("default");
    else iconPicker.panel.hidden = true;
  });
  iconPicker.app.addEventListener("click", () => requestIcons("app"));
  el("iconWindows").addEventListener("click", () => requestIcons("windows"));
  const browse = el("iconFile");
  el("iconBrowse").addEventListener("click", () => browse.click());
  browse.addEventListener("change", () => {
    if (browse.value) requestIcons("file", pickedPath(browse));
    browse.value = "";
  });
  el("iconReset").addEventListener("click", () => chooseIcon(""));
  el("iconClose").addEventListener("click", () => { iconSeq++; iconPicker.panel.hidden = true; });
}

function requestIcons(source, file) {
  iconSeq++;
  iconFile = null;
  iconPicker.grid.replaceChildren();
  iconPicker.status.textContent = "";
  iconPicker.source.textContent = file || "";
  iconPicker.loading.hidden = false;
  iconPicker.panel.hidden = false;
  send("sendToPlugin", { request: "listIcons", source, file, seq: iconSeq });
}

function showIcons(payload) {
  if (payload.seq !== iconSeq) return;
  iconFile = payload.iconFile;
  iconPicker.source.textContent = iconFile || "";
  iconPicker.app.disabled = !payload.appFile;
  for (const icon of payload.icons) {
    const tile = document.createElement("button");
    tile.type = "button";
    tile.className = "icon-tile";
    tile.title = icon.index === null ? iconFile : `${iconFile},${icon.index}`;
    if (payload.selected !== null && payload.selected !== undefined && (icon.index ?? -1) === payload.selected) tile.classList.add("selected");
    const img = document.createElement("img");
    img.src = `data:image/png;base64,${icon.png}`;
    tile.appendChild(img);
    tile.addEventListener("click", () => chooseIcon(tile.title));
    iconPicker.grid.appendChild(tile);
  }
  if (!payload.done) {
    iconPicker.status.textContent = `${payload.offset + payload.icons.length} of ${payload.total} icons`;
    return;
  }
  iconPicker.loading.hidden = true;
  iconPicker.status.textContent = payload.error ? payload.error : payload.total ? `${payload.total} icons` : "no icons in this file";
  iconPicker.grid.querySelector(".selected")?.scrollIntoView({ block: "nearest" });
}

function chooseIcon(value) {
  document.querySelector('[data-key="icon"]').value = value;
  saveSettings();
  iconSeq++;
  iconPicker.panel.hidden = true;
}

function applyEntry(entry) {
  for (const key of ENTRY_FIELDS) {
    const field = document.querySelector(`[data-key="${key}"]`);
    if (field) field.value = entry[key] === undefined || entry[key] === null ? "" : String(entry[key]);
  }
  saveSettings();
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

if (window.earlyConnect) connectElgatoStreamDeckSocket(...window.earlyConnect);
