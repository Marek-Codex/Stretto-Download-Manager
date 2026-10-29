// A small companion surface. The worker still owns the actual download handoff.
const api = globalThis.browser ?? globalThis.chrome;
const model = StrettoPopupModel;
const $ = (id) => document.getElementById(id);
const filters = [...document.querySelectorAll("[data-filter]")];
const mediaStore = () => api.storage.session ?? api.storage.local;
const state = {
  tabId: null,
  tabUrl: "",
  items: [],
  filter: "all",
  fragments: false,
  enabled: true,
  connection: "checking",
  connectionBusy: false,
  rules: { skipHosts: [] },
};
const busy = new Set();
let connectionRevision = 0;
const isPage = () =>
  model.page(state.tabUrl).supported && Number.isInteger(state.tabId);
const canSend = () => state.connection === "connected" && isPage();
const keyFor = (item) => `${item.tabId}:${item.url}`;
function say(message, kind = "") {
  $("status").textContent = message;
  $("status").className = kind;
}

function renderConnection() {
  const info = model.connection(state.connection, state.enabled, isPage());
  $("connection").className = `connection ${state.connection}`;
  $("connection-title").textContent = info.title;
  $("connection-detail").textContent = info.detail;
  $("connection-action").textContent = info.action;
  $("connection-action").hidden = !info.action;
  $("connection-action").disabled = state.connectionBusy;
  $("refresh").disabled = state.connectionBusy;
  $("monitoring").checked = state.enabled;
  $("capture-detail").textContent = state.enabled
    ? "Follow the desktop app’s capture rules."
    : "Paused. Downloads stay in the browser.";
  for (const id of ["all-links", "all-images"])
    $(id).disabled = !canSend() || busy.has(id);
  $("site").hidden = state.connection !== "connected" || !isPage();
  const host = model.page(state.tabUrl).host;
  const site = model.site(host);
  $("site-host").textContent = site;
  const skipped = (state.rules?.skipHosts || []).some(
    (h) => h && (host === h || host.endsWith(`.${h}`) || site === h),
  );
  $("site-capture").checked = !skipped;
  $("site-capture").disabled = !state.enabled || busy.has("site");
  $("site-detail").textContent = !state.enabled
    ? "Browser capture is paused."
    : skipped
      ? "Leave this site’s downloads in the browser."
      : "Include this site in browser capture.";
}

function renderMedia() {
  const page = model.page(state.tabUrl);
  $("page-title").textContent = page.supported
    ? `Media on ${page.host.replace(/^www\./, "")}`
    : "Media on this page";
  $("page-title").title = state.tabUrl;
  const all = isPage()
    ? model.media(state.items, state.tabId, "all", state.fragments)
    : [];
  for (const button of filters) {
    const kind = button.dataset.filter;
    $(`count-${kind}`).textContent = String(
      kind === "all"
        ? all.length
        : all.filter((item) => item.kind === kind).length,
    );
    button.setAttribute("aria-pressed", String(kind === state.filter));
  }
  const shown = all.filter(
    (item) => state.filter === "all" || item.kind === state.filter,
  );
  const names = model.names(shown);
  const list = $("list");
  list.replaceChildren();
  list.hidden = shown.length === 0;
  $("empty").hidden = shown.length > 0;
  $("clear").hidden =
    !isPage() || !state.items.some((item) => item?.tabId === state.tabId);
  $("clear").disabled = busy.has("clear");
  $("empty-title").textContent = !isPage()
    ? "Open a website to get started"
    : all.length
      ? "Nothing in this filter"
      : "Nothing detected yet";
  $("empty-detail").textContent = !isPage()
    ? "Browser settings and internal pages cannot be collected."
    : all.length
      ? "Try another media type."
      : "Play media on this page, then check here.";
  shown.forEach((item, index) => {
    const row = document.createElement("button");
    row.type = "button";
    row.className = `media-row ${item.kind}`;
    row.disabled = !canSend() || busy.has(keyFor(item));
    const info = model.describe(item);
    row.setAttribute(
      "aria-label",
      `Send ${names[index]} to Stretto, ${info.detail}`,
    );
    row.title = canSend()
      ? "Send to Stretto"
      : "Connect Stretto to send this media";
    const badge = document.createElement("span");
    badge.className = "badge";
    badge.textContent = info.badge;
    badge.setAttribute("aria-hidden", "true");
    const meta = document.createElement("span");
    meta.className = "grow";
    const name = document.createElement("div");
    name.className = "name";
    name.textContent = names[index];
    name.title = item.url;
    const detail = document.createElement("div");
    detail.className = "detail";
    detail.textContent = info.detail;
    meta.append(name, detail);
    const arrow = document.createElement("span");
    arrow.className = "send";
    arrow.textContent = "↓";
    arrow.setAttribute("aria-hidden", "true");
    row.append(badge, meta, arrow);
    row.addEventListener("click", () => sendItem(item));
    list.append(row);
  });
}
function render() {
  renderConnection();
  renderMedia();
}

async function refreshConnection() {
  const revision = ++connectionRevision;
  state.connectionBusy = true;
  renderConnection();
  try {
    const result = await api.runtime.sendMessage({ type: "stretto-status" });
    if (revision !== connectionRevision) return;
    state.connection = ["connected", "unpaired"].includes(result?.state)
      ? result.state
      : "offline";
    const cfg = await api.storage.local.get({ rules: { skipHosts: [] } });
    if (revision === connectionRevision) state.rules = cfg.rules;
  } catch {
    if (revision === connectionRevision) state.connection = "offline";
  } finally {
    if (revision === connectionRevision) {
      state.connectionBusy = false;
      render();
    }
  }
}
async function sendItem(item) {
  const key = keyFor(item);
  if (!canSend() || busy.has(key)) return;
  busy.add(key);
  renderMedia();
  say(`Sending ${item.name || "media"}…`);
  try {
    const result = await api.runtime.sendMessage({
      type: "stretto-send",
      url: item.url,
      tabUrl: state.tabUrl,
      tabId: item.tabId,
      kind: item.kind,
    });
    if (result?.ok) say("Sent to Stretto.", "ok");
    else
      say(
        result?.error === "declined"
          ? "Stretto did not accept this download."
          : "Could not send. Check the connection and try again.",
        "bad",
      );
  } catch {
    say("Could not reach Stretto. Check that the app is running.", "bad");
  } finally {
    busy.delete(key);
    renderMedia();
  }
}
async function collect(images) {
  const id = images ? "all-images" : "all-links";
  if (!canSend() || busy.has(id)) return;
  busy.add(id);
  renderConnection();
  say(images ? "Collecting images…" : "Collecting links…");
  try {
    const result = await api.runtime.sendMessage({
      type: "stretto-grab-all",
      tabId: state.tabId,
      images,
    });
    say(
      result?.message ||
        (result?.ok
          ? "Sent to Stretto for review."
          : "Could not collect this page."),
      result?.ok ? "ok" : "bad",
    );
  } catch {
    say("Could not reach Stretto.", "bad");
  } finally {
    busy.delete(id);
    renderConnection();
  }
}
$("monitoring").addEventListener("change", async () => {
  const enabled = $("monitoring").checked;
  $("monitoring").disabled = true;
  try {
    await api.storage.local.set({ enabled });
    state.enabled = enabled;
    say(enabled ? "Browser capture is on." : "Browser capture is paused.");
  } catch {
    say("Could not save the capture setting.", "bad");
  } finally {
    $("monitoring").disabled = false;
    renderConnection();
  }
});
$("site-capture").addEventListener("change", async () => {
  if (!canSend() || !state.enabled || busy.has("site")) return;
  const host = $("site-host").textContent,
    skip = !$("site-capture").checked;
  busy.add("site");
  renderConnection();
  try {
    const result = await api.runtime.sendMessage({
      type: "stretto-skip-host",
      host,
      skip,
    });
    if (!result?.ok) throw new Error();
    state.rules = (
      await api.storage.local.get({ rules: { skipHosts: [] } })
    ).rules;
    say(
      skip
        ? `Downloads from ${host} stay in the browser.`
        : `Downloads from ${host} go to Stretto again.`,
      "ok",
    );
  } catch {
    say("Could not change this site’s capture rule.", "bad");
  } finally {
    busy.delete("site");
    renderConnection();
  }
});
$("connection-action").addEventListener("click", async () => {
  if (state.connectionBusy) return;
  if (state.connection === "offline") {
    window.location.href = "stretto://launch";
    window.close();
    return;
  }
  state.connectionBusy = true;
  renderConnection();
  say("Check Stretto for the connection request.");
  try {
    await api.runtime.sendMessage({ type: "stretto-connect" });
  } catch {
    /* Status below explains where we landed. */
  }
  await refreshConnection();
});
$("refresh").addEventListener("click", refreshConnection);
$("opts").addEventListener("click", () => api.runtime.openOptionsPage());
$("all-links").addEventListener("click", () => collect(false));
$("all-images").addEventListener("click", () => collect(true));
for (const button of filters)
  button.addEventListener("click", () => {
    state.filter = button.dataset.filter;
    renderMedia();
  });
$("clear").addEventListener("click", async () => {
  if (!isPage() || busy.has("clear")) return;
  busy.add("clear");
  renderMedia();
  try {
    const result = await api.runtime.sendMessage({
      type: "stretto-clear",
      tabId: state.tabId,
    });
    if (!result?.ok) throw new Error();
    state.items = state.items.filter((item) => item?.tabId !== state.tabId);
    say("Page list cleared.");
  } catch {
    say("Could not clear the list. Try again.", "bad");
  } finally {
    busy.delete("clear");
    renderMedia();
  }
});
api.storage.onChanged.addListener((changes, area) => {
  let changed = false;
  if (area === "local") {
    if (changes.enabled) {
      state.enabled = changes.enabled.newValue !== false;
      changed = true;
    }
    if (changes.rules) {
      state.rules = changes.rules.newValue;
      changed = true;
    }
    if (changes.popupShowAll) {
      state.fragments = changes.popupShowAll.newValue === true;
      changed = true;
    }
  }
  if (area === (api.storage.session ? "session" : "local") && changes.media) {
    state.items = Array.isArray(changes.media.newValue)
      ? changes.media.newValue
      : [];
    changed = true;
  }
  if (changed) render();
});
async function init() {
  try {
    const [tab] = await api.tabs.query({ active: true, currentWindow: true });
    state.tabId = tab?.id;
    state.tabUrl = tab?.url || "";
  } catch {
    /* Unknown tabs must not expose media from all the other tabs. */
  }
  try {
    const cfg = await api.storage.local.get({
      enabled: true,
      popupShowAll: false,
      rules: { skipHosts: [] },
    });
    state.enabled = cfg.enabled !== false;
    state.fragments = cfg.popupShowAll === true;
    state.rules = cfg.rules;
    const stored = await mediaStore().get({ media: [] });
    state.items = Array.isArray(stored.media) ? stored.media : [];
  } catch {
    say("Some saved settings could not be loaded.", "bad");
  }
  $("version").textContent = api.runtime.getManifest().version;
  render();
  await refreshConnection();
}
init();
