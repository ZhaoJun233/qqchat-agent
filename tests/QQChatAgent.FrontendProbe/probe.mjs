/**
 * 面板前端冒烟测试（无需浏览器）
 *
 * 为什么需要它：
 *   面板的“保存设置”有一条隐性契约 —— **loadSettings() 回填的字段集合，
 *   必须覆盖 saveSettings() 发送的字段集合**。
 *   一旦某个字段只在保存时被读取、却没在加载时被回填，它的值就是空的：
 *   `Number("")` → 0 → 服务端把该设置压到最小值；若是白名单，则直接清空
 *   （严格模式 = 忽略全部消息）并删掉所有会话。这类问题在浏览器里表现为
 *   “设置保存不了”，而且没有任何报错，极难排查。
 *
 * 本脚本用最小 DOM 桩把 app.js 真跑一遍，静态 + 动态两道检查：
 *   1. app.js 引用的每个 DOM id 都必须在 index.html 中存在
 *   2. 动态：boot() 不抛异常；loadSettings() 后每个待保存字段都有合法值；
 *      保存时确实发出 POST 且 payload 与表单一致；表单未加载时保存被拒绝
 *
 * 运行：node tests/QQChatAgent.FrontendProbe/probe.mjs
 * 退出码：0 = 全部通过，1 = 有失败项
 */
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../../src/QQChatAgent.Headless/wwwroot");
const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
const js = fs.readFileSync(path.join(root, "app.js"), "utf8");

let pass = 0;
const failures = [];

function check(desc, ok, detail) {
  if (ok) {
    pass++;
    console.log("  ✓ " + desc);
  } else {
    failures.push(desc + (detail ? `　→ ${detail}` : ""));
    console.log("  ✗ " + desc + (detail ? `\n      → ${detail}` : ""));
  }
}

/* ─────────── 1) id 静态检查 ─────────── */

console.log("▶ 静态：DOM id 引用完整性");
const htmlIds = new Set([...html.matchAll(/id="([A-Za-z0-9_-]+)"/g)].map((m) => m[1]));
const jsIds = new Set([...js.matchAll(/\$\("([A-Za-z0-9_-]+)"\)/g)].map((m) => m[1]));
const missingIds = [...jsIds].filter((id) => !htmlIds.has(id));
check(`app.js 引用的 ${jsIds.size} 个 id 全部存在`, missingIds.length === 0, missingIds.join(", "));

/* ─────────── 1b) 保存按钮必须常驻可见 ─────────── */

console.log("\n▶ 静态：保存按钮必须常驻可见（不能藏在滚动区底部）");

const css = fs.readFileSync(path.join(root, "app.css"), "utf8");

/** 取出某个 class 元素及其完整内部 HTML（用 <div> 深度计数找闭合） */
function elementHtml(source, cls) {
  const at = source.indexOf(`class="${cls}"`);
  if (at < 0) return null;
  const start = source.lastIndexOf("<", at);
  let depth = 0;
  for (let i = start; i < source.length; ) {
    if (source.startsWith("<div", i)) { depth++; i += 4; }
    else if (source.startsWith("</div>", i)) { depth--; i += 6; if (depth === 0) return source.slice(start, i); }
    else i++;
  }
  return null;
}

const innerHtml = elementHtml(html, "settings-inner");
const footerHtml = elementHtml(html, "settings-footer");

check("设置页有常驻操作栏 .settings-footer", footerHtml !== null);
check("设置页用纵向布局（page-column）", /class="page page-column"\s+id="pageSettings"/.test(html));
check("★ 保存按钮在常驻操作栏里（不随内容滚动）", !!footerHtml && footerHtml.includes('id="saveBtn"'));
check(
  "★ 保存按钮不在可滚动的内容区里",
  !!innerHtml && !innerHtml.includes('id="saveBtn"'),
  "它被放回了 settings-inner —— 页面一长就又会「没有保存按钮」"
);
check("成功提示条也在常驻栏里", !!footerHtml && footerHtml.includes('id="saveBar"'));
check("CSS：.page-column 纵向", /\.page-column\s*\{[^}]*flex-direction:\s*column/.test(css));
check("CSS：.settings-footer 不参与缩放", /\.settings-footer\s*\{[^}]*flex:\s*0 0 auto/.test(css));
check(
  "CSS：.settings-scroll 可滚动且不被撑开",
  /\.settings-scroll\s*\{[^}]*overflow-y:\s*auto/.test(css) && /\.settings-scroll\s*\{[^}]*min-height:\s*0/.test(css)
);

/* ─────────── 1b2) 分节导航（卡片一多就得滑半天） ─────────── */

console.log("\n▶ 静态：设置页分节导航");

const navMatch = html.match(/<nav class="section-nav" id="settingsNav"[^>]*>\s*<\/nav>/);
check("设置页有分节导航容器 #settingsNav", navMatch !== null);
check(
  "★ 导航在滚动区外面（内容是斜着滚的，它不能跟着跑）",
  html.indexOf('id="settingsNav"') > 0 &&
    html.indexOf('id="settingsNav"') < html.indexOf('class="settings-scroll"'),
  "它得是 #pageSettings 的直子元素，排在 .settings-scroll 前面"
);
check(
  "CSS：导航常驻不缩放 + 窄屏可横向滑（手机上 10 个胶囊放不下）",
  /\.section-nav\s*\{[^}]*flex:\s*0 0 auto/.test(css) && /\.section-nav\s*\{[^}]*overflow-x:\s*auto/.test(css)
);
check("CSS：胶囊有 hover 与 active 态（高亮当前所在的那一节）",
  /\.section-link:hover/.test(css) && /\.section-link\.active/.test(css));
check(
  "★ JS：胶囊从卡片标题生成（不维护第二份硬编码列表）",
  js.includes('initSettingsNav') && js.includes('"settingsNav"') && /querySelector\("h3"\)/.test(js),
  "加一张卡片就得同步改一遍导航列表的话，早晚会对不上"
);
check("JS：boot() 里真的调用了", /initSettingsNav\(\);/.test(js));
check(
  "★ 宽屏不留大片空白：设置页多列排布按窗口宽度自动铺",
  /columns:\s*420px/.test(css) && /break-inside:\s*avoid/.test(css),
  "写死 2 列 / 1240px 上限时，宽屏右边会空一大块；grid 又会把矮卡片下方留空"
);

/* ─────────── 1c) 扫码登录入口必须在面板里 ─────────── */

console.log("\n▶ 静态：QQ 未登录时能在面板里直接扫码");

check("index.html 有扫码卡片容器", html.includes('id="loginCard"') && html.includes('id="loginQrImg"'));
check("设置页 NapCat 状态条有「在面板里扫码登录」入口", html.includes('id="napcatLoginBtn"'));
check("CSS：二维码用白底容器（深色主题下二维码反色就扫不出来了）",
  /\.login-qr\s*\{[^}]*background:\s*#fff/.test(css));
check("CSS：.ghost-btn 只属于面板自身（不与浏览器默认按钮撞样式）", /\.ghost-btn\s*\{/.test(css));

/* ─────────── 1d) 手机端适配 ─────────── */

console.log("\n▶ 静态：手机端适配");

check("viewport 带 viewport-fit=cover（iPhone 刘海/手势条不被盖）", /viewport-fit=cover/.test(html));
check("底部标签栏存在，且桌面端隐藏", html.includes('class="mtabbar"') && /\.mtabbar\s*\{[^}]*display:\s*none/.test(css));
check("聊天页有返回键", html.includes('id="chatBack"'));
check("★ 窄屏主从式切换：列表/聊天二选一（靠 body.m-chat-open 控制）",
  /body\.m-chat-open \.conv-pane\s*\{[^}]*display:\s*none/.test(css) &&
  /body\.m-chat-open \.chat-pane\s*\{[^}]*display:\s*flex/.test(css) &&
  /state\.chatOpen/.test(js));
check("★ 手机端输入框字号 ≥16px（否则 iOS 聚焦时会把页面放大）",
  /@media \(max-width: 760px\)[\s\S]*?input,\s*textarea,\s*select\s*\{[^}]*font-size:\s*16px/.test(css));
check("处理了安全区 env(safe-area-inset-bottom)", /env\(safe-area-inset-bottom/.test(css));
check("★ 长按也能弹会话菜单（iOS 不触发 contextmenu）", /touchstart/.test(js) && /suppressClick/.test(js));
check("点进会话会切到聊天视图", /state\.chatOpen = true/.test(js));
check("去设置页会收起聊天视图", /state\.chatOpen = false/.test(js));

/* ─────────── 1e) 表情包 ─────────── */

console.log("\n▶ 静态：表情包（自动收集 + 按语境发 + 自巡检）");

check("设置页有表情包卡片（启用 / 上限 / 候选数 / 巡检间隔 / 发送冷却）",
  html.includes('id="setEnableStickers"') && html.includes('id="setStickerMax"') &&
  html.includes('id="setStickerCandidates"') && html.includes('id="setStickerCurate"') &&
  html.includes('id="setStickerCooldown"'));
check("模型三项改成面板可改（不再是 readonly 输入框）",
  !/id="setBaseUrl"[^>]*readonly/.test(html) && !/id="setModel"[^>]*readonly/.test(html) &&
  html.includes('id="setApiKey"') && /<input[^>]*type="password"[^>]*id="setApiKey"|<input[^>]*id="setApiKey"[^>]*type="password"/.test(html) &&
  html.includes('id="clearApiKey"'));

check("设置页有戳一戳卡片（启用 / 冷却 / 当前心情 / 心情保留）",
  html.includes('id="setEnablePoke"') && html.includes('id="setPokeCooldown"') &&
  html.includes('id="setMood"') && html.includes('id="setMoodTtl"'));
check("有表情包库弹层 + 手动入口（巡检 / 导入）",
  html.includes('id="stickerModal"') && html.includes('id="stickerGrid"') &&
  html.includes('id="stickerCurateNow"') && html.includes('id="stickerImportAlbum"'));
check("★ 面板用的是库接口而不是自带一套存储",
  js.includes('"/api/stickers"') && js.includes('/delete') && js.includes('/api/stickers/import'));
check("库图片走带令牌的地址（<img> 不能带自定义头）",
  /withToken\(`\/api\/stickers\//.test(js));
check("CSS：缩略图白底（透明 PNG 看得清）", /\.sticker-cell\s*\{[^}]*background:\s*#ffffff/.test(css));
check("★ 轮询不能因为标签页隐藏就停（否则切到手机去扫时二维码会停在旧的那张）",
  !/document\.hidden\s*\)\s*return/.test(js), "轮询里又出现了 document.hidden 提前 return");
check("回到标签页时立即对一次二维码状态", /visibilitychange/.test(js));
check("★ 二维码要显示“多久前更新/可能已过期”，否则死码与活码长得一样",
  /可能已过期/.test(js) && /ageSeconds/.test(js));

/* ─────────── 2) 回填覆盖检查（核心） ─────────── */

console.log("\n▶ 静态：保存字段必须都在加载时回填");

// 从 saveSettings 的 payload 字面量里提取 “字段名 -> 读取的 DOM id”
const saveBody = js.slice(js.indexOf("async function saveSettings"), js.indexOf("/* ─────────── 日志"));
const saveFields = [...saveBody.matchAll(/(\w+):\s*(Number\()?\$\("([A-Za-z0-9_]+)"\)\.(value|checked)/g)]
  .map((m) => ({ key: m[1], numeric: !!m[2], id: m[3], prop: m[4] }));

// loadSettings 里被赋值（回填）的 id
const loadBody = js.slice(js.indexOf("async function loadSettings"), js.indexOf("async function saveSettings"));
const filledIds = new Set([...loadBody.matchAll(/\$\("([A-Za-z0-9_]+)"\)\.(?:value|checked)\s*=/g)].map((m) => m[1]));

check("能从 saveSettings 解析出待保存字段", saveFields.length > 0, `解析到 ${saveFields.length} 个`);
const notFilled = saveFields.filter((f) => !filledIds.has(f.id));
check(
  `待保存的 ${saveFields.length} 个字段全部在 loadSettings 中回填`,
  notFilled.length === 0,
  notFilled.map((f) => `${f.key}(${f.id})`).join(", ")
);

/* ─────────── 3) 动态：真跑一遍 ─────────── */

console.log("\n▶ 动态：启动 → 进设置页 → 保存");

const domReady = [];
const navItems = [];
const mtabItems = [];
const navClicks = [];
const saveClicks = [];
const classCalls = [];   // 记录 classList 上的调用，用来验证手机端视图状态的初始化

const handlers = new Map();
function fire(id, type, ev) {
  const list = handlers.get(`${id}:${type}`) || [];
  for (const fn of list) fn(ev || { target: { id } });
}

function makeEl(id, tag = "div") {
  return {
    id,
    tagName: tag.toUpperCase(),
    value: "",
    checked: false,
    textContent: "",
    innerHTML: "",
    hidden: false,
    disabled: false,
    className: "",
    src: "",
    alt: "",
    title: "",
    scrollTop: 0,
    scrollHeight: 100,
    clientHeight: 100,
    dataset: {},
    style: {},
    children: [],
    classList: {
      add(c) { classCalls.push(["add", c]); },
      remove(c) { classCalls.push(["remove", c]); },
      toggle(c, on) { classCalls.push(["toggle", c, on]); },
      contains: () => false
    },
    addEventListener(type, fn) {
      const key = `${id}:${type}`;
      if (!handlers.has(key)) handlers.set(key, []);
      handlers.get(key).push(fn);
      if (id === "saveBtn" && type === "click") saveClicks.push(fn);
      if ((id.startsWith("nav-") || id.startsWith("mtab-")) && type === "click") {
        navClicks.push({ page: id.replace(/^(nav|mtab)-/, ""), fn });
      }
    },
    removeEventListener() {},
    appendChild(c) { this.children.push(c); return c; },
    insertBefore(c) { this.children.unshift(c); return c; },
    replaceChildren() { this.children = []; },
    querySelector: () => makeEl(id + ":child"),
    querySelectorAll: () => [],
    focus() {},
    remove() {},
    closest: () => null,
    getAttribute: () => null,
    setAttribute() {},
    scrollIntoView() {}
  };
}

const elCache = new Map();
const document = {
  getElementById(id) {
    if (!htmlIds.has(id)) return undefined; // 与真浏览器一致：取不到就是 undefined
    if (!elCache.has(id)) elCache.set(id, makeEl(id));
    return elCache.get(id);
  },
  querySelector: () => null,
  querySelectorAll(sel) {
    // app.js 用 ".navitem"（桌面左栏）或 ".navitem, .mtab"（含手机底部标签栏）
    if (sel !== ".navitem" && sel !== ".navitem, .mtab") return [];
    if (navItems.length === 0) {
      for (const page of ["chat", "settings"]) {
        const b = makeEl("nav-" + page);
        b.dataset = { page };
        navItems.push(b);
      }
      for (const page of ["chat", "settings"]) {
        const b = makeEl("mtab-" + page);
        b.dataset = { page };
        mtabItems.push(b);
      }
    }

    return sel === ".navitem" ? navItems : navItems.concat(mtabItems);
  },
  createElement: (t) => makeEl("created", t),
  createDocumentFragment: () => makeEl("frag"),
  hidden: false,
  addEventListener(type, fn) { if (type === "DOMContentLoaded") domReady.push(fn); },
  removeEventListener() {},
  body: makeEl("body"),
  documentElement: makeEl("html")
};

const RUNTIME = {
  botPersona: "老群友", messageWhitelist: "123,456", aiDesire: 50, suitabilityThreshold: 10,
  aiModeEnabled: true, maxTokens: 4096, groupCooldownSeconds: 8, privateCooldownSeconds: 3,
  idleFallbackSeconds: 60, splitReplies: true, segmentDelayMs: 700, maxContextMessages: 200,
  profileLookupCount: 8, profileSummaryLines: 8, maxProfileChars: 1200,
  maxMessagesPerConversation: 500, maxConcurrentReplies: 2, enableProfileSummary: true,
  profileSummaryThreshold: 20, profileSummaryMaxChars: 160, profileSummaryIntervalSeconds: 120,
  enableStickers: true, stickerLibraryMax: 120, stickerCandidates: 6, stickerCurateIntervalSeconds: 3600,
  stickerCooldownSeconds: 120, enablePoke: true, pokeCooldownSeconds: 45, mood: "", moodTtlSeconds: 7200
};
const ENV = {
  modelBaseUrl: "http://x/v1", modelBaseUrlSource: "env",
  model: "m", modelSource: "panel",
  apiKeyMasked: "sk-1****", apiKeySet: true, apiKeySource: "panel",
  oneBotProtocol: "ForwardWebSocket",
  oneBotAddress: "ws://napcat:3001", oneBotTokenMasked: "", uin: "10001", dataDir: "/data",
  healthPort: 8080, tz: "Asia/Shanghai"
};

const calls = [];
const statusPayload = {
  status: {
    onebot: { connected: true, protocol: "ForwardWebSocket" },
    account: { uin: "10001", selfId: 10001, online: false },
    agent: { enabled: true, model: "mock" }
  },
  aiMode: true,
  conversations: []
};
const qrPayload = {
  ok: true, configured: true,
  url: "https://txz.qq.com/p?k=TESTKEY&f=1600001615",
  ageSeconds: 4, key: "abc123def456", error: null
};
const fetchStub = async (url, opts) => {
  const target = String(url);
  const method = (opts && opts.method) || "GET";
  calls.push({ url: target, method, body: opts && opts.body });
  let payload = statusPayload;
  if (target.includes("/api/settings")) {
    payload = { runtime: RUNTIME, env: ENV, settingsFile: "/data/data/settings.json" };
  } else if (target.includes("/api/qqlogin")) {
    payload = qrPayload;
  }
  return { ok: true, status: 200, text: async () => JSON.stringify(payload) };
};

const store = new Map();
let confirmAnswer = true;
const sandbox = {
  document,
  location: { href: "http://127.0.0.1:8080/?token=test-token", reload() {}, pathname: "/", search: "?token=test-token", hash: "" },
  history: { replaceState() {} },
  localStorage: { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => store.set(k, v) },
  fetch: fetchStub,
  EventSource: class { constructor() {} addEventListener() {} },
  URL, URLSearchParams, console, setTimeout, clearTimeout,
  setInterval: (fn, ms) => setTimeout(fn, ms),
  clearInterval: (id) => clearTimeout(id),
  requestAnimationFrame: (fn) => setTimeout(fn, 0),
  confirm: () => confirmAnswer,
  prompt: () => null,
  alert: () => {},
  matchMedia: () => ({ matches: false, addEventListener() {}, addListener() {}, removeEventListener() {} }),
  JSON, Date, Math, Object, Array, String, Number, Boolean, Map, Set, Promise, RegExp, Error
};
sandbox.window = sandbox;
sandbox.globalThis = sandbox;

let loadError = null;
try {
  vm.createContext(sandbox);
  vm.runInContext(js, sandbox, { filename: "app.js" });
} catch (e) {
  loadError = `${e.name}: ${e.message}`;
}
check("app.js 能无异常加载", loadError === null, loadError);

let bootError = null;
for (const fn of domReady) {
  try { await fn(); } catch (e) { bootError = `${e.name}: ${e.message}`; }
}
await new Promise((r) => setTimeout(r, 400));
check("boot() 无异常", bootError === null, bootError);
check("注册了保存按钮的点击处理", saveClicks.length === 1, `实际 ${saveClicks.length} 个`);
check("★ boot() 会把手机端视图初始化成列表（body.m-chat-open）",
  classCalls.some((c) => c[0] === "toggle" && c[1] === "m-chat-open"),
  JSON.stringify(classCalls.slice(0, 6)));

/* ─────────── 3b) 动态：扫码登录卡片 ─────────── */

console.log("\n▶ 动态：QQ 未登录时必须能直接在面板里扫码");

const loginCard = document.getElementById("loginCard");
const loginImg = document.getElementById("loginQrImg");

check("未登录时扫码卡片自动出现", loginCard.hidden === false);
check("★ 二维码来自面板自己的接口（浏览器不直连 NapCat：跨域 + 另一道认证）",
  String(loginImg.src).includes("/api/qqlogin/qrcode.svg"), String(loginImg.src));
check("★ 二维码地址必须带令牌（<img> 不能带自定义头，令牌只能走查询参数；漏了它就只在生产环境挂）",
  String(loginImg.src).includes("token=test-token"), String(loginImg.src));
check("二维码按指纹做缓存键（同一张图不重载，否则扫到一半会闪）",
  String(loginImg.src).includes("k=abc123def456"), String(loginImg.src));
check("卡片给出可复制的二维码链接（扫不动时兜底）",
  String(document.getElementById("loginUrl").textContent).includes("txz.qq.com"),
  document.getElementById("loginUrl").textContent);

// 账号上线 → 卡片必须自己消失（别让用户以为还掉线）
statusPayload.status.account.online = true;
for (const fn of domReady) {
  try { await fn(); } catch (e) { /* 同一次 boot，上面的断言已经覆盖 */ }
}
await new Promise((r) => setTimeout(r, 200));
check("★ 账号上线后扫码卡片自动消失", loginCard.hidden === true);

// 进设置页（触发 loadSettings 回填）
let navError = null;
const nav = navClicks.find((n) => n.page === "settings");
if (nav) {
  try { nav.fn({}); } catch (e) { navError = `${e.name}: ${e.message}`; }
}
await new Promise((r) => setTimeout(r, 400));
check("进入设置页无异常", navError === null, navError);

const readField = (f) => {
  const el = document.getElementById(f.id);
  if (el === undefined) return undefined;
  if (f.prop === "checked") return el.checked;
  return f.numeric ? Number(el.value) : String(el.value);
};

// 每个字段读出来的值，必须等于服务端刚返回的值（这才是“回填正确”的真正定义）
const wrongFill = saveFields.filter((f) => {
  if (RUNTIME[f.key] === undefined) return false; // env 类字段不在 runtime 里
  return readField(f) !== RUNTIME[f.key];
});
check(
  `回填后 ${saveFields.filter((f) => RUNTIME[f.key] !== undefined).length} 个字段与服务器值一致`,
  wrongFill.length === 0,
  wrongFill.map((f) => `${f.key}: 表单=${JSON.stringify(readField(f))} 服务器=${JSON.stringify(RUNTIME[f.key])}`).join("; ")
);

// 保存
const before = calls.length;
try { await saveClicks[0]({}); } catch (e) { /* 由下面的断言体现 */ }
await new Promise((r) => setTimeout(r, 300));

const saveCall = calls.slice(before).find((c) => c.method === "POST" && c.url.includes("/api/settings"));
check("点击保存确实发出了 POST /api/settings", !!saveCall);

if (saveCall) {
  const payload = JSON.parse(saveCall.body);
  check("payload 字段数与表单一致", Object.keys(payload).length === saveFields.length,
    `${Object.keys(payload).length} vs ${saveFields.length}`);

  // 核心不变式：**什么都不改直接保存，payload 必须与服务端当前值完全一致**。
  // 一旦有字段没被回填，它就会以 0/空/false 发回来 → 服务端把它压到最小值
  // （白名单被清空则直接进入严格模式并删光会话）。
  const drifted = Object.entries(payload).filter(([k, v]) => RUNTIME[k] !== undefined && v !== RUNTIME[k]);
  check(
    "★ 不改动直接保存，payload 与服务端值完全一致（没被空值污染）",
    drifted.length === 0,
    drifted.map(([k, v]) => `${k}: 发出=${JSON.stringify(v)} 服务器=${JSON.stringify(RUNTIME[k])}`).join("; ")
  );

  check("payload 携带了真实人设", payload.botPersona === RUNTIME.botPersona, String(payload.botPersona));
  check("payload 携带了真实白名单", payload.messageWhitelist === RUNTIME.messageWhitelist, String(payload.messageWhitelist));
  check("payload 携带了真实 maxTokens", payload.maxTokens === RUNTIME.maxTokens, String(payload.maxTokens));
}

/* ─────────── 4) 未保存修改的提示与拦截 ─────────── */

console.log("\n▶ 动态：未保存修改的提示与离开拦截");

const dirtyHint = document.getElementById("dirtyHint");
check("刚加载完不显示「未保存」提示", dirtyHint.hidden === true);

// 编辑一个字段 → 应出现提示
fire("pageSettings", "input", { target: { id: "setPersona" } });
check("编辑后显示「未保存」提示", dirtyHint.hidden === false);

// 有未保存修改时离开设置页 → 应被拦下
confirmAnswer = false;
fire("nav-chat", "click");
check("★ 有未保存修改时离开设置页会被拦下（不再“自动复原”）", document.getElementById("pageSettings").hidden === false);

// 确认后再离开
confirmAnswer = true;
fire("nav-chat", "click");
check("确认后可以离开", document.getElementById("pageSettings").hidden === true);

// 回到设置页（重新加载 → 提示清掉），且改主题不算未保存
fire("nav-settings", "click");
await new Promise((r) => setTimeout(r, 300));
check("回到设置页后提示被清掉", dirtyHint.hidden === true);
fire("pageSettings", "change", { target: { id: "setTheme" } });
check("改主题不会被当成未保存的修改", dirtyHint.hidden === true);

/* ─────────── 汇总 ─────────── */

console.log("");
if (failures.length === 0) {
  console.log(`通过 ${pass}，失败 0`);
  process.exit(0);
}

console.log(`通过 ${pass}，失败 ${failures.length}`);
for (const f of failures) console.log("  ✗ " + f);
process.exit(1);
