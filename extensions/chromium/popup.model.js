// Presentation rules only. Discovery, pairing and acceptance belong to the worker.
globalThis.StrettoPopupModel = Object.freeze({
  page(address) {
    try {
      const url = new URL(address);
      return { supported: /^https?:$/.test(url.protocol), host: url.hostname };
    } catch {
      return { supported: false, host: "" };
    }
  },
  // Keep site matching aligned with capture's siteOf in common.js.
  site(host) {
    const labels = String(host || "")
      .toLowerCase()
      .replace(/^\.+|\.+$/g, "")
      .split(".");
    if (labels.length <= 2 || /^[\d.]+$/.test(host) || host.includes(":"))
      return String(host || "").toLowerCase();
    const countryCode = labels.at(-1).length === 2;
    const secondLevel = [
      "co",
      "com",
      "org",
      "net",
      "gov",
      "ac",
      "edu",
      "ne",
      "or",
    ].includes(labels.at(-2));
    return labels.slice(countryCode && secondLevel ? -3 : -2).join(".");
  },
  media(items, tabId, filter = "all", fragments = false) {
    if (!Number.isInteger(tabId)) return [];
    return (Array.isArray(items) ? items : [])
      .filter(
        (item) =>
          item &&
          item.tabId === tabId &&
          this.page(item.url).supported &&
          (fragments || item.kind !== "segment") &&
          (filter === "all" || item.kind === filter),
      )
      .sort((a, b) => (Number(b.seenAt) || 0) - (Number(a.seenAt) || 0));
  },
  describe(item) {
    let ext = "";
    try {
      ext =
        /\.([a-z0-9]+)$/i
          .exec(new URL(item.url).pathname)?.[1]
          ?.toLowerCase() || "";
    } catch {
      /* No container to label. */
    }
    const badge =
      item.kind === "stream"
        ? { mpd: "DASH", m3u8: "HLS" }[ext] || "STREAM"
        : item.kind === "segment"
          ? "PART"
          : ext
            ? ext.slice(0, 4).toUpperCase()
            : item.kind === "audio"
              ? "AUDIO"
              : "VIDEO";
    const kind =
      {
        stream: "Stream playlist · needs ffmpeg",
        segment: "Stream fragment · not a complete file",
        audio: "Audio file",
        video: "Video file",
      }[item.kind] || "Media file";
    const size = Number(item.size),
      units = ["B", "KB", "MB", "GB"];
    const power =
      Number.isFinite(size) && size >= 1
        ? Math.min(3, Math.floor(Math.log(size) / Math.log(1024)))
        : -1;
    return {
      badge,
      detail:
        power < 0
          ? kind
          : `${kind} · ${(size / 1024 ** power).toFixed(power > 0 ? 1 : 0)} ${units[power]}`,
    };
  },
  names(items) {
    const counts = new Map(),
      seen = new Map();
    for (const item of items) {
      const name = item.name || "Untitled media";
      counts.set(name, (counts.get(name) || 0) + 1);
    }
    return items.map((item) => {
      const name = item.name || "Untitled media";
      const n = (seen.get(name) || 0) + 1;
      seen.set(name, n);
      return counts.get(name) > 1 ? `${name} (${n})` : name;
    });
  },
  connection(state, enabled, supported) {
    if (state === "connected")
      return {
        title: "Connected to Stretto",
        detail: !enabled
          ? "Capture paused. Manual sends still work."
          : supported
            ? "Ready to send to the desktop app."
            : "Open a website to collect files.",
        action: "",
      };
    if (state === "unpaired")
      return {
        title: "One connection to make",
        detail: "Allow this extension in Stretto.",
        action: "Connect",
      };
    if (state === "offline")
      return {
        title: "Stretto is not running",
        detail: "Start the app, then reopen this popup.",
        action: "Start Stretto",
      };
    return {
      title: "Finding Stretto…",
      detail: "Checking the desktop app.",
      action: "",
    };
  },
});
