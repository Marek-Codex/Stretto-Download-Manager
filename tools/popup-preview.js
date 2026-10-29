// Local visual fixture: real popup assets, fake browser APIs. Never bundled.
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const root = path.join(__dirname, "../extensions/chromium");
const fixture = (mode) => `<script>
const listeners=[];
const cfg={enabled:${mode !== "paused"},popupShowAll:false,rules:{skipHosts:[]},notifications:true};
let media=${JSON.stringify([
  {
    tabId: 7,
    url: "https://media.example/aurora.mp4",
    kind: "video",
    name: "Aurora — a small moment of quiet",
    size: 18400000,
    seenAt: 3,
  },
  {
    tabId: 7,
    url: "https://media.example/night.mp3",
    kind: "audio",
    name: "Night drive · soundtrack",
    size: 6200000,
    seenAt: 2,
  },
  {
    tabId: 7,
    url: "https://media.example/master.m3u8",
    kind: "stream",
    name: "Community stream replay",
    seenAt: 1,
  },
])};
${mode === "empty" ? "media=[];" : ""}
const store=(data,area)=>({get:async defaults=>({...defaults,...data}),set:async patch=>{Object.assign(data,patch);listeners.forEach(fn=>fn(Object.fromEntries(Object.entries(patch).map(([k,v])=>[k,{newValue:v}])),area));}});
globalThis.chrome={tabs:{query:async()=>[{id:7,url:'${mode === "browser" ? "chrome://settings" : "https://community.example/watch"}'}]},storage:{local:store(cfg,'local'),session:{get:async()=>({media})},onChanged:{addListener:fn=>listeners.push(fn)}},runtime:{getManifest:()=>({version:'1.5.0'}),openOptionsPage:async()=>{location.href='/options.html'},sendMessage:async msg=>{
if(msg.type==='stretto-status')return{state:'${["offline", "unpaired"].includes(mode) ? mode : "connected"}'};
if(msg.type==='stretto-clear'){media=[];return{ok:true};}
if(msg.type==='stretto-skip-host'){cfg.rules={skipHosts:msg.skip?[msg.host]:[]};return{ok:true};}
return{ok:true,message:'Sent to Stretto for review.'};
}}};
</script>`;
const allowed = new Set([
  "popup.html",
  "popup.css",
  "popup.js",
  "popup.model.js",
  "options.html",
  "options.js",
  "icon48.png",
]);
http
  .createServer((req, res) => {
    const url = new URL(req.url, "http://localhost");
    const name = url.pathname === "/" ? "popup.html" : url.pathname.slice(1);
    if (!allowed.has(name)) {
      res.writeHead(404);
      return res.end();
    }
    let body = fs.readFileSync(path.join(root, name));
    if (name.endsWith(".html"))
      body = body
        .toString()
        .replace(
          "<head>",
          "<head>" + fixture(url.searchParams.get("state") || "connected"),
        );
    res.setHeader(
      "Content-Type",
      name.endsWith(".html")
        ? "text/html; charset=utf-8"
        : name.endsWith(".css")
          ? "text/css"
          : name.endsWith(".js")
            ? "text/javascript"
            : "image/png",
    );
    res.end(body);
  })
  .listen(8107, "127.0.0.1", () =>
    console.log(
      "Popup visual fixture: http://127.0.0.1:8107/ (?state=offline|unpaired|paused|empty|browser)",
    ),
  );
