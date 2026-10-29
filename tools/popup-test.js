// Run the actual popup controller against a tiny DOM and browser API fixture.
// The fixture does not imitate capture: that gets its own worker regressions.
const fs = require("node:fs");
const vm = require("node:vm");
const assert = require("node:assert/strict");
const path = require("node:path");
const root = path.join(__dirname, "../extensions/chromium");
class Element {
  constructor() {
    this.children = [];
    this.handlers = {};
    this.dataset = {};
    this.disabled = false;
    this.hidden = false;
    this.textContent = "";
  }
  addEventListener(type, fn) {
    this.handlers[type] = fn;
  }
  setAttribute(name, value) {
    this[name] = value;
  }
  append(...nodes) {
    this.children.push(...nodes);
  }
  replaceChildren(...nodes) {
    this.children = nodes;
  }
  async click() {
    if (!this.disabled) await this.handlers.click?.();
  }
}
const tick = () => new Promise((resolve) => setImmediate(resolve));
async function fixture({
  connection = "connected",
  tab = { id: 7, url: "https://example.com/watch" },
  session = true,
} = {}) {
  const elements = {};
  for (const match of fs
    .readFileSync(path.join(root, "popup.html"), "utf8")
    .matchAll(/id="([^"]+)"/g))
    elements[match[1]] = new Element();
  const filters = ["all", "video", "audio", "stream"].map((kind) => {
    const button = new Element();
    button.dataset.filter = kind;
    return button;
  });
  const items = [
    {
      tabId: 7,
      url: "https://cdn.example/a.mp4",
      kind: "video",
      name: "<img onerror=bad()>",
      seenAt: 1,
    },
    {
      tabId: 7,
      url: "https://cdn.example/a.mp3",
      kind: "audio",
      name: "Audio",
      seenAt: 2,
    },
    {
      tabId: 7,
      url: "https://cdn.example/master.m3u8",
      kind: "stream",
      name: "Stream",
      seenAt: 3,
    },
    {
      tabId: 7,
      url: "https://cdn.example/part.ts",
      kind: "segment",
      name: "Fragment",
      seenAt: 4,
    },
    {
      tabId: 8,
      url: "https://other.example/private.mp4",
      kind: "video",
      name: "Other tab",
      seenAt: 5,
    },
  ];
  const local = {
    enabled: true,
    popupShowAll: false,
    rules: { skipHosts: [] },
    ...(session ? {} : { media: items }),
  };
  const data = { media: items };
  let storageListener, pendingSend;
  const messages = [];
  const responses = {
    "stretto-send": { ok: true },
    "stretto-clear": { ok: true },
    "stretto-grab-all": { ok: true, message: "Batch ready." },
  };
  const storage = (values, area) => ({
    get: async (defaults) => ({ ...defaults, ...values }),
    set: async (patch) => {
      Object.assign(values, patch);
      storageListener?.(
        Object.fromEntries(
          Object.entries(patch).map(([k, v]) => [k, { newValue: v }]),
        ),
        area,
      );
    },
  });
  const api = {
    tabs: { query: async () => (tab ? [tab] : []) },
    storage: {
      local: storage(local, "local"),
      onChanged: {
        addListener: (fn) => {
          storageListener = fn;
        },
      },
    },
    runtime: {
      getManifest: () => ({ version: "1.5.0" }),
      openOptionsPage: async () => {},
      sendMessage: async (message) => {
        messages.push(message);
        if (message.type === "stretto-status") return { state: connection };
        if (message.type === "stretto-send" && pendingSend) return pendingSend;
        return responses[message.type] || { ok: true };
      },
    },
  };
  if (session) api.storage.session = storage(data, "session");
  const context = vm.createContext({
    chrome: api,
    URL,
    console,
    window: { location: {}, close() {} },
    document: {
      getElementById: (id) => {
        assert.ok(elements[id], id);
        return elements[id];
      },
      querySelectorAll: () => filters,
      createElement: () => new Element(),
    },
  });
  for (const name of ["popup.model.js", "popup.js"])
    vm.runInContext(fs.readFileSync(path.join(root, name), "utf8"), context);
  await tick();
  return {
    elements,
    filters,
    messages,
    responses,
    model: context.StrettoPopupModel,
    api,
    update: (changes, area = session ? "session" : "local") =>
      storageListener(changes, area),
    deferSend: (promise) => {
      pendingSend = promise;
    },
  };
}
(async () => {
  const f = await fixture(),
    e = f.elements;
  assert.equal(e.list.children.length, 3, "fragments and other tabs stay out");
  assert.equal(
    e.list.children[2].children[1].children[0].textContent,
    "<img onerror=bad()>",
    "titles are literal text",
  );
  await f.filters[2].click();
  assert.equal(e.list.children.length, 1);
  await e.list.children[0].click();
  assert.deepEqual(JSON.parse(JSON.stringify(f.messages.at(-1))), {
    type: "stretto-send",
    url: "https://cdn.example/a.mp3",
    tabUrl: "https://example.com/watch",
    tabId: 7,
    kind: "audio",
  });
  assert.equal(e.status.textContent, "Sent to Stretto.");
  f.responses["stretto-send"] = { ok: false, error: "declined" };
  await e.list.children[0].click();
  assert.match(e.status.textContent, /did not accept/);
  let release;
  f.deferSend(
    new Promise((resolve) => {
      release = resolve;
    }),
  );
  const sending = e.list.children[0].click();
  f.update({
    media: {
      newValue: [
        {
          tabId: 7,
          url: "https://cdn.example/a.mp3",
          kind: "audio",
          name: "Updated",
        },
      ],
    },
  });
  assert.equal(
    e.list.children[0].disabled,
    true,
    "live update cannot duplicate an in-flight send",
  );
  release({ ok: true });
  await sending;
  f.responses["stretto-clear"] = { ok: false };
  await e.clear.click();
  assert.equal(e.list.children.length, 1, "failed clear retains visible list");
  f.responses["stretto-clear"] = { ok: true };
  await e.clear.click();
  assert.equal(e.list.children.length, 0);
  const fallback = await fixture({ session: false });
  assert.equal(fallback.elements.list.children.length, 3);
  fallback.update({ popupShowAll: { newValue: true } }, "local");
  assert.equal(
    fallback.elements.list.children.length,
    4,
    "advanced preference updates live",
  );
  fallback.elements.monitoring.checked = false;
  await fallback.elements.monitoring.handlers.change();
  assert.equal(
    fallback.elements["all-links"].disabled,
    false,
    "pausing automatic capture preserves manual collection",
  );
  assert.equal(fallback.elements["site-capture"].disabled, true);
  await fallback.elements["all-images"].click();
  assert.deepEqual(JSON.parse(JSON.stringify(fallback.messages.at(-1))), {
    type: "stretto-grab-all",
    tabId: 7,
    images: true,
  });
  assert.equal(fallback.elements.status.textContent, "Batch ready.");
  let finishCollection;
  fallback.responses["stretto-grab-all"] = new Promise((resolve) => {
    finishCollection = resolve;
  });
  const collection = fallback.elements["all-links"].click();
  fallback.update({ enabled: { newValue: true } }, "local");
  assert.equal(
    fallback.elements["all-links"].disabled,
    true,
    "live settings cannot duplicate collection",
  );
  finishCollection({ ok: false });
  await collection;
  assert.equal(fallback.elements.status.className, "bad");
  assert.equal(fallback.elements["all-links"].disabled, false);
  for (const state of ["offline", "unpaired"]) {
    const x = await fixture({ connection: state });
    assert.equal(x.elements["all-links"].disabled, true);
    assert.equal(x.elements.list.children[0].disabled, true);
    assert.equal(x.elements["connection-action"].hidden, false);
  }
  for (const tab of [
    null,
    { id: 7, url: "chrome://settings" },
    { id: 7, url: "about:preferences" },
  ]) {
    const x = await fixture({ tab });
    assert.equal(x.elements.list.children.length, 0);
    assert.equal(x.elements["all-links"].disabled, true);
    await x.elements.clear.click();
    assert.ok(!x.messages.some((message) => message.type === "stretto-clear"));
  }
  assert.equal(
    f.model.describe({ kind: "stream", url: "https://cdn.example/playlist" })
      .badge,
    "STREAM",
  );
  assert.equal(f.model.site("video.example.co.uk"), "example.co.uk");
  console.log(
    "PASS popup: tab isolation, filters, safe titles, handoff, decline, in-flight updates, clear failure, fallback storage, preferences, paused/offline/unpaired/internal states",
  );
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
