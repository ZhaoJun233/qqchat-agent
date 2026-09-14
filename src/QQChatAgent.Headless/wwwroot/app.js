/* ══════════════════════════════════════════════════════════════
   QQ Chat Agent · Web 面板
   与桌面版 ViewModel 对应的前端状态机：
     会话列表 / 消息气泡 / AI 开关 / 设置持久化 / SSE 实时同步
   ══════════════════════════════════════════════════════════════ */
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  // 桌面版 ChatPageViewModel.GradientPairs：无 QQ 头像时的渐变底色
  const GRADIENTS = [
    ["#5E5CE6", "#8E6CEF"], ["#0EA5E9", "#22D3EE"], ["#10B981", "#34D399"],
    ["#F59E0B", "#FBBF24"], ["#EF4444", "#F97316"], ["#EC4899", "#8B5CF6"]
  ];

  const state = {
    conversations: [],
    byKey: new Map(),
    activeKey: null,
    messages: new Map(),      // key -> [msg]
    status: null,
    aiMode: true,
    search: "",
    logs: [],
    settingsLoaded: false,    // 设置表单是否已从服务端回填过
    chatOpen: false,          // 手机端：是否已点进某个会话（列表 ↔ 聊天 的主从切换）
    login: {                  // 扫码登录卡片
      qr: null,               // /api/qqlogin 的响应
      collapsed: false,       // 用户手动收起过
      greeted: false          // 上线提示只说一次
    },
    stickers: null            // 表情包库（打开弹层时拉）
  };

  /* ─────────── 工具 ─────────── */

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function timeText(ms) {
    const d = new Date(ms);
    const now = new Date();
    const sameDay = d.toDateString() === now.toDateString();
    const p = (n) => String(n).padStart(2, "0");
    return sameDay
      ? `${p(d.getHours())}:${p(d.getMinutes())}`
      : `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function hashIndex(key) {
    let h = 0;
    for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) >>> 0;
    return h % GRADIENTS.length;
  }

  function toast(text) {
    const el = $("toast");
    el.textContent = text;
    el.hidden = false;
    clearTimeout(el._t);
    el._t = setTimeout(() => { el.hidden = true; }, 2200);
  }

  /* ─────────── 面板访问令牌（可选） ───────────
     服务端设了 QQCHAT_PANEL_TOKEN 后，面板需要令牌。
     首次用 http://…/?token=你的令牌 打开即可，之后自动记住。 */
  const TOKEN_KEY = "qqchat.panel.token";

  (function captureTokenFromUrl() {
    try {
      const url = new URL(location.href);
      const t = url.searchParams.get("token");
      if (!t) return;
      localStorage.setItem(TOKEN_KEY, t.trim());
      url.searchParams.delete("token");
      history.replaceState(null, "", url.pathname + url.search + url.hash);
    } catch { /* 无 localStorage / 无 history：忽略 */ }
  })();

  function panelToken() {
    try { return localStorage.getItem(TOKEN_KEY) || ""; } catch { return ""; }
  }

  /// EventSource 不能自定义请求头 → 令牌只能走查询参数
  function withToken(path) {
    const t = panelToken();
    if (!t) return path;
    return path + (path.includes("?") ? "&" : "?") + "token=" + encodeURIComponent(t);
  }

  function authHeaders(extra) {
    const t = panelToken();
    return Object.assign({}, extra || {}, t ? { "X-Panel-Token": t } : {});
  }

  /// 401 时向用户要一次令牌（避免直接报错让人摸不着头脑）
  function askForToken() {
    const t = prompt("面板需要访问令牌（服务端设置了 QQCHAT_PANEL_TOKEN）：");
    if (!t) return;
    try { localStorage.setItem(TOKEN_KEY, t.trim()); } catch { /* 忽略 */ }
    location.reload();
  }

  async function api(path, options) {
    const opts = options || {};
    const res = await fetch(withToken(path), {
      ...opts,
      headers: authHeaders(Object.assign({ "Content-Type": "application/json" }, opts.headers))
    });
    if (res.status === 401) { askForToken(); throw new Error("需要访问令牌"); }
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { /* 非 JSON */ }
    if (!res.ok) throw Object.assign(new Error(`HTTP ${res.status}`), { status: res.status, data });
    return data;
  }

  /* ─────────── 手机端主从切换 ───────────
     窄屏下会话列表与聊天区不再并排（一屏放不下）：
     默认只看列表，点进会话才切到聊天，头部有返回键，底部有标签栏。
     桌面端一切照旧 —— 这里只是给 <body> 挂一个 class，由 CSS 媒体查询决定要不要理它。 */
  function syncMobileView() {
    document.body.classList.toggle("m-chat-open", !!state.chatOpen);
  }

  /* ─────────── 表情包库（面板） ───────────
     库是全局共用一份（不分会话）。面板只负责“看”和“手动干预”：
     自动收集/按语境发送/自巡检都在机器人那边，这里只看结果。 */

  async function openStickerLib() {
    $("stickerModal").hidden = false;
    $("stickerGrid").textContent = "";
    const empty = document.createElement("div");
    empty.className = "sticker-empty";
    empty.textContent = "加载中…";
    $("stickerGrid").appendChild(empty);
    await refreshStickerLib();
  }

  async function refreshStickerLib() {
    try {
      state.stickers = await api("/api/stickers");
      renderStickerLib();
    } catch (e) {
      $("stickerStat").textContent = "读取失败：" + e.message;
    }
  }

  function renderStickerLib() {
    const d = state.stickers || { items: [] };
    $("stickerStat").textContent =
      `共 ${d.count}/${d.max} 张（已识别 ${d.described}，待识别 ${d.pendingDescribe}）`;

    const grid = $("stickerGrid");
    grid.textContent = "";

    const items = d.items || [];
    if (items.length === 0) {
      const empty = document.createElement("div");
      empty.className = "sticker-empty";
      empty.textContent =
        "还没有表情包。群友在群里发表情/图片时会自动收进来（去重 + 自动识别）；也可以点上面的「导入收藏表情」。";
      grid.appendChild(empty);
      return;
    }

    for (const item of items) {
      const cell = document.createElement("div");
      cell.className = "sticker-cell";

      const img = document.createElement("img");
      img.alt = "";
      img.loading = "lazy";
      img.src = withToken(`/api/stickers/${encodeURIComponent(item.id)}/img`);
      img.addEventListener("error", () => img.remove());

      const cap = document.createElement("div");
      cap.className = "cap";
      const tags = (item.tags || []).join("/");
      cap.textContent = (item.desc || "（还没识别）") + (tags ? "｜" + tags : "");
      const uses = document.createElement("span");
      uses.className = "uses";
      uses.textContent = ` ·用过${item.uses}次`;
      cap.appendChild(uses);

      const del = document.createElement("button");
      del.className = "del";
      del.textContent = "✕";
      del.title = "删除这张表情包";
      del.addEventListener("click", async () => {
        if (!confirm("删除这张表情包？")) return;
        try {
          await api(`/api/stickers/${encodeURIComponent(item.id)}/delete`, { method: "POST" });
          await refreshStickerLib();
        } catch (e) {
          toast("删除失败：" + e.message);
        }
      });

      cell.appendChild(img);
      cell.appendChild(cap);
      cell.appendChild(del);
      grid.appendChild(cell);
    }
  }

  /// 让机器人自己巡检一遍（它会看一遍库，自己决定删哪些）
  async function requestStickerCurate() {
    try {
      await api("/api/stickers/curate", { method: "POST" });
      toast("已开始巡检，结果会记在运行日志里");
      setTimeout(refreshStickerLib, 4000);
    } catch (e) {
      toast("巡检失败：" + e.message);
    }
  }

  /// 从登录账号的 QQ 收藏表情导入（机器人自己“添加”表情包的来源）
  async function requestStickerImport() {
    try {
      await api("/api/stickers/import", { method: "POST" });
      toast("正在从 QQ 收藏表情导入，稍后刷新看结果");
      setTimeout(refreshStickerLib, 6000);
    } catch (e) {
      toast("导入失败：" + e.message);
    }
  }

  /* ─────────── 会话列表 ─────────── */

  // 按 key 复用 DOM 节点，避免每次事件都重建整个列表（重建会重载头像图、强制重排）
  const convNodes = new Map();
  let convSignature = "";

  function convSignatureOf(items) {
    return items.map((c) =>
      `${c.key}|${c.name}|${c.preview}|${c.unread}|${c.thinking ? 1 : 0}|${c.lastTime}`
    ).join("~") + `#${state.activeKey}#${state.search}`;
  }

  function createConvNode(c) {
    const el = document.createElement("div");
    el.className = "conv";
    el.dataset.key = c.key;
    el.innerHTML =
      `<div class="avatar av-42"><span class="av-text"></span><img alt="" loading="lazy" />` +
      `<div class="conv-main"><div class="conv-row"><span class="conv-name"></span>` +
      `<span class="conv-time"></span></div><div class="conv-row">` +
      `<span class="conv-preview"></span><span class="conv-tail"></span></div></div>`;
    const img = el.querySelector("img");
    img.addEventListener("error", () => { img.remove(); });
    el.addEventListener("click", () => {
      // 长按弹菜单后浏览器还会补一个 click：别让它顺手把会话切走
      if (el.dataset.suppressClick === "1") { el.dataset.suppressClick = "0"; return; }
      selectConversation(el.dataset.key);
    });
    el.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      openContextMenu(e.clientX, e.clientY, el.dataset.key);
    });

    // 手机没有右键：长按 450ms 弹同一张菜单（iOS Safari 不触发 contextmenu）
    let pressTimer = 0;
    const cancelPress = () => { if (pressTimer) { clearTimeout(pressTimer); pressTimer = 0; } };
    el.addEventListener("touchstart", (e) => {
      const t = e.touches && e.touches[0];
      if (!t) return;
      cancelPress();
      pressTimer = setTimeout(() => {
        pressTimer = 0;
        el.dataset.suppressClick = "1";
        openContextMenu(t.clientX, t.clientY, el.dataset.key);
      }, 450);
    }, { passive: true });
    el.addEventListener("touchend", cancelPress, { passive: true });
    el.addEventListener("touchcancel", cancelPress, { passive: true });
    el.addEventListener("touchmove", cancelPress, { passive: true });
    return el;
  }

  function updateConvNode(el, c) {
    const [g1, g2] = GRADIENTS[hashIndex(c.key)];
    el.classList.toggle("active", c.key === state.activeKey);

    const av = el.querySelector(".avatar");
    av.style.background = `linear-gradient(135deg,${g1},${g2})`;
    el.querySelector(".av-text").textContent = c.avatarText;

    // 头像地址未变就不动 img，避免重复下载/闪烁
    const img = av.querySelector("img");
    if (img) {
      const want = c.avatarUrl || "";
      if (img.dataset.url !== want) {
        img.dataset.url = want;
        if (want) img.src = want; else img.remove();
      }
    } else if (c.avatarUrl) {
      const fresh = document.createElement("img");
      fresh.alt = "";
      fresh.loading = "lazy";
      fresh.dataset.url = c.avatarUrl;
      fresh.addEventListener("error", () => fresh.remove());
      fresh.src = c.avatarUrl;
      av.appendChild(fresh);
    }

    el.querySelector(".conv-name").textContent = c.name;
    el.querySelector(".conv-time").textContent = timeText(c.lastTime);
    el.querySelector(".conv-preview").textContent = c.preview || "（暂无消息）";

    const tail = el.querySelector(".conv-tail");
    const wantTail = c.thinking ? "think" : c.unread > 0 ? "badge" : "none";
    if (tail.dataset.kind !== wantTail) {
      tail.dataset.kind = wantTail;
      tail.className = wantTail === "think" ? "conv-think" : wantTail === "badge" ? "badge" : "";
      tail.textContent = "";
    }

    if (wantTail === "badge") {
      const text = c.unread > 99 ? "99+" : String(c.unread);
      if (tail.textContent !== text) tail.textContent = text;
      tail.className = "badge";
    } else if (wantTail === "think" && tail.textContent !== "思考中…") {
      tail.textContent = "思考中…";
    }
  }

  function renderConversations(force) {
    const q = state.search.trim().toLowerCase();
    const items = state.conversations.filter((c) =>
      !q || c.name.toLowerCase().includes(q) || (c.preview || "").toLowerCase().includes(q));

    $("convEmpty").hidden = state.conversations.length > 0;

    // 内容未变就什么都不做（SSE 会高频重复触发）
    const sig = convSignatureOf(items);
    if (!force && sig === convSignature) return;
    convSignature = sig;

    const list = $("convList");
    const seen = new Set();
    for (const c of items) {
      seen.add(c.key);
      let el = convNodes.get(c.key);
      if (!el) {
        el = createConvNode(c);
        convNodes.set(c.key, el);
      }

      updateConvNode(el, c);
      // appendChild 对已有子节点是“移动”，不会重建；循环结束自然按 items 排序
      list.appendChild(el);
    }

    for (const [key, el] of convNodes) {
      if (!seen.has(key)) {
        el.remove();
        convNodes.delete(key);
      }
    }
  }

  /* ─────────── 消息区 ─────────── */

  // 头部头像：地址未变就不重建 <img>，避免每次状态推送都重新下载
  let hdrAvatarKey = null;

  function renderHeader() {
    const c = state.byKey.get(state.activeKey);
    const connected = !!state.status?.onebot?.connected;
    const uin = state.status?.account?.uin || "-";

    if (!c) {
      $("hdrName").textContent = "未选择会话";
      $("hdrKindTag").hidden = true;
      $("hdrMeta").textContent = "从左侧选择一个会话";
      const d0 = connDisplay();
      $("hdrStatus").textContent = `${d0.short}${state.aiMode ? " · AI 自动回复开" : " · AI 自动回复关"}`;
      $("hdrDot").className = "dot " + d0.dot;
      $("hdrAvatar").style.background = "linear-gradient(135deg,#808080,#a0a0a0)";
      if (hdrAvatarKey !== null) {
        $("hdrAvatar").replaceChildren(Object.assign(document.createElement("span"), { id: "hdrAvatarText", textContent: "?" }));
        hdrAvatarKey = null;
      }
      return;
    }

    const [g1, g2] = GRADIENTS[hashIndex(c.key)];
    $("hdrName").textContent = c.name;
    const tag = $("hdrKindTag");
    tag.hidden = false;
    tag.textContent = c.kind === "Group" ? "群聊" : "私聊";
    tag.className = "kind-tag" + (c.kind === "Group" ? "" : " private");
    $("hdrMeta").textContent = `${c.kind === "Group" ? "QQ 群聊" : "QQ 私聊"} · 当前账号 QQ ${uin}`;
    const d1 = connDisplay();
    $("hdrStatus").textContent = `${d1.short}${state.aiMode ? " · AI 自动回复开" : " · AI 自动回复关"}`;
    $("hdrDot").className = "dot " + (c.thinking ? "busy" : d1.dot);

    if (hdrAvatarKey !== c.key) {
      hdrAvatarKey = c.key;
      const av = $("hdrAvatar");
      av.style.background = `linear-gradient(135deg,${g1},${g2})`;
      av.replaceChildren(Object.assign(document.createElement("span"), { id: "hdrAvatarText", textContent: c.avatarText }));
      if (c.avatarUrl) {
        const img = document.createElement("img");
        img.alt = "";
        img.addEventListener("error", () => img.remove());
        img.src = c.avatarUrl;
        av.appendChild(img);
      }
    }
  }

  function createMessageNode(m) {
    const el = document.createElement("div");
    const role = m.role.toLowerCase();
    el.className = "msg " + role;

    if (role === "self") {
      el.innerHTML = `<div class="bubble"></div><div class="msg-time">${timeText(m.time)}</div>`;
      fillBubble(el.querySelector(".bubble"), m);
      return el;
    }

    if (role === "system") {
      el.innerHTML = `<div class="bubble"></div>`;
      fillBubble(el.querySelector(".bubble"), m);
      return el;
    }

    el.innerHTML =
      (m.senderName ? `<div class="msg-sender">${esc(m.senderName)}</div>` : "") +
      `<div class="bubble"></div><div class="msg-time">${timeText(m.time)}</div>`;

    if (m.senderId) {
      const senderEl = el.querySelector(".msg-sender");
      if (senderEl) {
        const uid = document.createElement("span");
        uid.className = "uid";
        uid.title = "查看人物档案";
        uid.textContent = m.senderId;
        uid.addEventListener("click", () => showProfile(m.senderId));
        senderEl.appendChild(uid);
      }
    }

    fillBubble(el.querySelector(".bubble"), m);
    return el;
  }

  function fillBubble(bubble, m) {
    if (m.recalled) {
      // 已撤回：面板是运维视角 —— 原文看得到（方便排查），但要划掉并说清楚
      // “模型看到的是 [已撤回]”，否则操作者会以为机器人看过这条内容。
      const orig = document.createElement("span");
      orig.className = "recalled-text";
      orig.textContent = m.text || "";
      bubble.appendChild(orig);

      const tag = document.createElement("div");
      tag.className = "recall-tag";
      tag.textContent = "已撤回" + (m.recallOperator ? `（${m.recallOperator}）` : "") + " · 模型看到的是「[已撤回] 原文」";
      bubble.appendChild(tag);
    } else {
      bubble.textContent = m.text || "";
    }

    for (const u of m.images || []) {
      const img = document.createElement("img");
      img.alt = "";
      img.loading = "lazy";
      img.addEventListener("error", () => img.remove());
      img.src = u;
      bubble.appendChild(img);
    }
  }

  function isNearBottom() {
    const s = $("msgScroll");
    return s.scrollHeight - s.scrollTop - s.clientHeight < 120;
  }

  function renderMessages() {
    const box = $("msgList");
    const list = state.messages.get(state.activeKey) || [];
    $("chatEmpty").hidden = !!(state.activeKey && list.length > 0);

    const nearBottom = isNearBottom();
    const frag = document.createDocumentFragment();
    for (const m of list) {
      frag.appendChild(createMessageNode(m));
    }

    box.replaceChildren(frag);
    if (nearBottom) scrollToBottom();
  }

  /// 实时消息到达时只追加一个节点，不重建整个列表（避免滚动中收到消息就顿一下）
  function appendMessageNode(m) {
    const nearBottom = isNearBottom();
    $("msgList").appendChild(createMessageNode(m));
    $("chatEmpty").hidden = true;
    if (nearBottom) scrollToBottom();
  }

  function scrollToBottom() {
    const s = $("msgScroll");
    s.scrollTop = s.scrollHeight;
  }

  function renderThinking() {
    const c = state.byKey.get(state.activeKey);
    const t = $("thinking");
    if (c?.thinking) {
      $("thinkingText").textContent = "AI 正在思考…";
      t.hidden = false;
    } else {
      t.hidden = true;
    }
  }

  function renderAiMode() {
    const btn = $("aiToggle");
    btn.classList.toggle("on", state.aiMode);
    $("aiLabel").textContent = state.aiMode ? "AI 开" : "AI 关";
    renderHeader();
  }

  /* ─────────── 扫码登录卡片 ───────────
     为什么把二维码搬进面板：
       远程部署时 NapCat 的登录二维码只在它自己的 WebUI（另一个域名 + 另一道 Basic 认证）里看得到，
       用户打开机器人面板只看到“没有会话”，根本不知道要先扫码 —— 这是卡住最久的地方。
     实现要点：
       · 二维码由服务端从 NapCat WebUI 取（/api/qqlogin），浏览器不直连 NapCat（那条路有跨域 + 认证）
       · 每 10 秒轮询一次轻量 JSON；**只有 key（二维码指纹）变了才换 <img> 的 src**，避免扫到一半被重载
       · 账号一旦上线，卡片自己消失（用户不需要记得回来关它） */

  let loginTimer = 0;
  let loginBusy = false;

  /// 只要不是“已确认在线”，就认为需要展示登录入口（含“未知”与“面板还没连上”）
  function needsLogin() {
    return state.status?.account?.online !== true;
  }

  function renderLoginCard() {
    const card = $("loginCard");
    if (!card) return;

    const show = needsLogin() && !state.login.collapsed;
    card.hidden = !show;
    if (!show) return;

    const qr = state.login.qr;
    const img = $("loginQrImg");
    const box = $("loginQrState");

    if (qr?.ok && qr.url) {
      const want = withToken(`/api/qqlogin/qrcode.svg?k=${encodeURIComponent(qr.key || "")}`);
      if (img.dataset.src !== want) {
        img.dataset.src = want;   // 同一张二维码不重载：重载会让正在扫的图闪一下
        img.src = want;
      }
      img.hidden = false;
      box.hidden = true;
      $("loginUrlRow").hidden = false;
      $("loginUrl").textContent = qr.url;

      // 把“这张码多新”直接告诉用户：以前面板不显示年龄，
      // 一张 25 分钟前的死码看起来和刚生成的一模一样，手机扫完只会说“已过期”。
      const age = Number(qr.ageSeconds || 0);
      if (qr.stale || age > 180) {
        $("loginTitle").textContent = "二维码可能已过期";
        $("loginMsg").textContent =
          `这张码已经 ${age} 秒没换新了（NapCat 那边好像没在轮换），手机扫会提示“已过期”。` +
          "点下面的「换一张二维码」试试；若仍不换，在服务器上重启协议端：docker compose restart napcat。";
      } else {
        $("loginTitle").textContent = "QQ 未登录 · 扫码即可上线";
        $("loginMsg").textContent =
          `用手机 QQ 扫左侧二维码登录机器人账号（这张码 ${age} 秒前更新，约 2 分钟有效，会自动换新）。` +
          "登录成功后本卡片自动消失，机器人随即开始收消息。";
      }
      return;
    }

    img.hidden = true;
    box.hidden = false;
    $("loginUrlRow").hidden = true;

    if (qr && qr.configured === false) {
      $("loginTitle").textContent = "面板里取不到二维码（未配置）";
      box.textContent = "未配置";
      $("loginMsg").textContent =
        "机器人容器没有 NapCat WebUI 令牌，所以无法直接显示登录二维码。" +
        "给容器补上 QQCHAT_NAPCAT_WEBUI_TOKEN（值见 napcat/config/webui.json 的 token）后重启即可；" +
        "也可以先用 NapCat 面板扫码。";
      return;
    }

    $("loginTitle").textContent = "二维码暂时取不到";
    box.textContent = qr?.error ? "读取失败" : "正在获取二维码…";
    $("loginMsg").textContent = qr?.error
      ? `${qr.error}（面板每 10 秒自动重试）`
      : "正在向 NapCat 索取登录二维码…";
  }

  async function pollLogin(force) {
    if (loginBusy || !needsLogin()) return;
    loginBusy = true;
    try {
      state.login.qr = await api("/api/qqlogin" + (force ? "?refresh=1" : ""));
    } catch (e) {
      state.login.qr = { ok: false, configured: true, error: "读取二维码失败：" + e.message };
    } finally {
      loginBusy = false;
      $("loginRefresh").disabled = false;
      renderLoginCard();
    }
  }

  function startLoginWatch() {
    if (loginTimer) return;
    loginTimer = setInterval(() => {
      // 刻意不看 document.hidden：用户往往是“看着面板 → 拿手机扫”，
      // 切到手机/另一个窗口时标签页就变成隐藏，后台不再轮询 -> 二维码停在旧的那张，
      // 扫出来就是“二维码已过期”（线上就是这么踩的）。
      // 后台标签页的定时器本来就会被浏览器降频，代价很小。
      pollLogin(false);
    }, 10000);
  }

  /// 回到这个标签页时立刻对一次，避免展示一张“回来时已经过期”的码。
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden && needsLogin()) pollLogin(false);
  });

  /* ─────────── 连接展示态 ───────────
     关键区分：**协议端连着 ≠ QQ 账号在线**。
     登录失效/被顶号时 WebSocket 依旧连着、get_login_info 也照旧回显 UIN，
     但消息一条都进不来 —— 只显示“已连接”会把用户带进死胡同。 */
  function connDisplay() {
    const connected = !!state.status?.onebot?.connected;
    const online = state.status?.account?.online; // true / false / null(未知)

    if (!connected) {
      return { level: "off", dot: "off", short: "离线 · 未连接协议端" };
    }
    if (online === false) {
      return { level: "warn", dot: "busy", short: "账号离线 · 需重新登录" };
    }
    return { level: "on", dot: "on", short: "在线" };
  }

  function renderConn() {
    const connected = !!state.status?.onebot?.connected;
    const uin = state.status?.account?.uin || "";
    const d = connDisplay();
    $("connDot").className = "dot " + d.dot;
    $("connText").textContent = d.level === "on"
      ? `在线${uin ? " · " + uin : ""}`
      : d.short;
    $("brandSub").textContent = state.status
      ? `Web 面板 · ${state.status.agent?.model || ""}`
      : "Web 面板";

    // 账号刚上线：说一声，再把扫码卡片收起来（否则用户会一直盯着那个「没登录」的卡片）
    if (d.level === "on" && !state.login.greeted) {
      state.login.greeted = true;
      if (state.login.qr) toast("QQ 已上线，机器人开始收消息");
    } else if (d.level !== "on") {
      state.login.greeted = false;
    }

    renderLoginCard();
    renderHeader();
  }

  /* ─────────── 交互 ─────────── */

  async function selectConversation(key) {
    state.activeKey = key;
    // 手机端：切到聊天视图（桌面端没影响，见 syncMobileView 的注释）
    state.chatOpen = true;
    syncMobileView();
    renderConversations();
    renderHeader();
    renderThinking();

    try {
      const data = await api(`/api/conversations/${encodeURIComponent(key)}/messages?limit=300`);
      state.messages.set(key, data.messages || []);
      renderMessages();
      scrollToBottom();
      await api(`/api/conversations/${encodeURIComponent(key)}/read`, { method: "POST" });
      const c = state.byKey.get(key);
      if (c) c.unread = 0;
      renderConversations();
    } catch (e) {
      toast("加载消息失败：" + e.message);
    }
  }

  async function sendMessage() {
    const input = $("input");
    const text = input.value.trim();
    if (!text || !state.activeKey) return;

    const btn = $("sendBtn");
    btn.disabled = true;
    try {
      await api(`/api/conversations/${encodeURIComponent(state.activeKey)}/send`, {
        method: "POST",
        body: JSON.stringify({ text })
      });
      input.value = "";
      autoGrow();
    } catch (e) {
      toast("发送失败：" + (e.data?.error || e.message));
    } finally {
      btn.disabled = false;
      input.focus();
    }
  }

  function autoGrow() {
    const input = $("input");
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 132) + "px";
  }

  let ctxKey = null;

  function openContextMenu(x, y, key) {
    ctxKey = key;
    const menu = $("ctxMenu");
    menu.hidden = false;
    const w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.min(x, innerWidth - w - 8) + "px";
    menu.style.top = Math.min(y, innerHeight - h - 8) + "px";
  }

  function closeContextMenu() {
    ctxKey = null;
    $("ctxMenu").hidden = true;
  }

  async function showProfile(uid) {
    try {
      const data = await api(`/api/profiles/${encodeURIComponent(uid)}`);
      $("profileTitle").textContent = `成员档案 · QQ ${uid}`;
      $("profileBody").textContent = data.summary || "（暂无档案：该成员还没有被记录发言）";
      $("profileModal").hidden = false;
    } catch (e) {
      toast("读取档案失败：" + e.message);
    }
  }

  /// 归档历史：已滑出滚动窗口的旧消息（不会被模型看到，但留档可查）
  async function showArchive(key) {
    try {
      const data = await api(`/api/archive?key=${encodeURIComponent(key)}&limit=300`);
      const name = state.byKey.get(key)?.name || key;
      $("profileTitle").textContent = `归档历史 · ${name}`;

      const list = data.messages || [];
      if (list.length === 0) {
        $("profileBody").textContent = data.error
          ? `${data.error}\n\n路径：${data.path || "-"}`
          : "（该会话尚无归档）";
      } else {
        const head = `共 ${data.totalLines} 条归档，显示最近 ${list.length} 条：\n\n`;
        $("profileBody").textContent = head + list.map((m) => {
          const d = new Date(m.t * 1000);
          const p = (n) => String(n).padStart(2, "0");
          const ts = `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
          const who = m.sender ? `${m.sender}(${m.uid || m.role})` : m.role;
          return `${ts}  ${who}\n       ${m.text}`;
        }).join("\n\n");
      }

      $("profileModal").hidden = false;
    } catch (e) {
      toast("读取归档失败：" + e.message);
    }
  }

  /* ─────────── 设置页 ─────────── */

  let settingsDirty = false;

  // 用户点了“清除密钥”：下次保存时把密钥清掉（输入框留空默认是“不改”，两者必须区分）
  let pendingApiKeyClear = false;

  function markSettingsDirty() {
    if (settingsDirty) return;
    settingsDirty = true;
    const h = $("dirtyHint");
    if (h) h.hidden = false;
  }

  function clearSettingsDirty() {
    settingsDirty = false;
    const h = $("dirtyHint");
    if (h) h.hidden = true;
  }

  /* ── 设置页分节导航 ──
     卡片一多，“找个设置项”就得滑上滑下。这里按每张卡片的 h3 生成一排跳转胶囊：
     点一下滚过去，滚动时自动高亮当前所在的那节。标题是读 DOM 的 —— 以后加卡片不用改这里。 */
  let refreshSettingsNav = null;

  function initSettingsNav() {
    const nav = $("settingsNav");
    const scroller = document.querySelector("#pageSettings .settings-scroll");
    const inner = scroller && scroller.querySelector(".settings-inner");
    if (!nav || !scroller || !inner || nav.dataset.ready === "1") return;

    const cards = Array.from(inner.querySelectorAll(":scope > .card"));
    if (cards.length < 2) return;   // 只有一两张卡片就不必导航了
    nav.dataset.ready = "1";

    function titleOf(card, i) {
      const h3 = card.querySelector("h3");
      if (!h3) return `第 ${i + 1} 节`;
      // h3 里常跟一个 <span class="hint">（例如“运行日志 · 最近 200 条”）—— 导航只要主标题
      const nodes = h3.childNodes ? Array.from(h3.childNodes) : [h3];
      const text = nodes
        .filter((n) => !(n.nodeType === 1 && n.classList && n.classList.contains("hint")))
        .map((n) => n.textContent || "")
        .join("")
        .replace(/\s+/g, " ")
        .trim();
      return text || `第 ${i + 1} 节`;
    }

    const links = cards.map((card, i) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "section-link";
      btn.textContent = titleOf(card, i);
      btn.title = btn.textContent;
      btn.addEventListener("click", () => {
        setActive(btn);
        card.scrollIntoView({ behavior: "smooth", block: "start" });
      });
      nav.appendChild(btn);
      return btn;
    });

    function setActive(active) {
      links.forEach((b) => b.classList.toggle("active", b === active));
      // 手机上这排胶囊是横向滑动的：把当前项带进可视区域，否则高亮了也看不见
      if (active && typeof active.scrollIntoView === "function") {
        active.scrollIntoView({ block: "nearest", inline: "nearest" });
      }
    }

    // “当前在哪一节” = **可视区里那个顶部最靠近视口顶部的卡片**。
    // 不用“最后一个越过顶部的卡片”那种算法：多列流里卡片是“填满一列再开下一列”，
    // DOM 顺序与视觉顺序不完全一致，滚到下一列的顶部时高亮会莫名其妙跳回去。
    function currentLink() {
      const box = scroller.getBoundingClientRect();
      let best = 0;
      let bestDist = Infinity;
      cards.forEach((card, i) => {
        const r = card.getBoundingClientRect();
        if (r.bottom < box.top + 8 || r.top > box.bottom - 8) return;   // 没在可视区里
        const dist = Math.abs(r.top - (box.top + 8));
        if (dist < bestDist) { bestDist = dist; best = i; }
      });
      return links[best];
    }

    refreshSettingsNav = () => setActive(currentLink());

    // 滚动时高亮（用 rAF 合并：滚动事件一秒能来上百次）
    const raf = window.requestAnimationFrame || ((fn) => setTimeout(fn, 16));
    let scheduled = false;
    scroller.addEventListener("scroll", () => {
      if (scheduled) return;
      scheduled = true;
      raf(() => {
        scheduled = false;
        setActive(currentLink());
      });
    }, { passive: true });

    setActive(links[0]);
  }

  async function loadSettings() {
    state.settingsLoaded = false; // 重新加载期间先封住保存
    const data = await api("/api/settings");
    const r = data.runtime, e = data.env;

    $("setBaseUrl").value = e.modelBaseUrl || "";
    // 密钥只在界面上显示掉掩码；输入框留空 = 不改（想清空点旁边的按钮）
    $("setApiKey").value = "";
    $("setApiKey").placeholder = e.apiKeySet
      ? e.apiKeyMasked + "（已设置，留空即不修改）"
      : "还没配密钥，在这里填一个";
    $("setModel").value = e.model || "";
    // 这三个值当前是从哪儿来的：面板改过就归面板，否则是容器环境变量
    $("baseUrlSrc").textContent = e.modelBaseUrlSource === "panel" ? "来自面板（保存后立即生效）" : "来自环境变量";
    $("modelSrc").textContent = e.modelSource === "panel" ? "来自面板（保存后立即生效）" : "来自环境变量";
    $("apiKeySrc").textContent = e.apiKeySource === "panel"
      ? "来自面板（存 data/secrets.json，权限 600）"
      : e.apiKeySource === "env" ? "来自环境变量" : "未配置";
    $("setProtocol").value = e.oneBotProtocol || "";
    $("setAddress").value = e.oneBotAddress || "";
    $("setToken").value = e.oneBotTokenMasked ? e.oneBotTokenMasked + "（来自环境变量）" : "未设置";
    $("setUin").value = e.uin || "自动识别";
    $("settingsPath").textContent = `配置文件：${data.settingsFile}`;

    $("setPersona").value = r.botPersona || "";
    $("setMaxTokens").value = r.maxTokens;
    $("setWhitelist").value = r.messageWhitelist || "";
    $("setDesire").value = r.aiDesire;
    $("desireVal").textContent = r.aiDesire;
    $("setThreshold").value = r.suitabilityThreshold;
    $("threshVal").textContent = r.suitabilityThreshold;
    $("setAiMode").checked = r.aiModeEnabled;
    $("setGroupCooldown").value = r.groupCooldownSeconds;
    $("setPrivateCooldown").value = r.privateCooldownSeconds;
    $("setIdleFallback").value = r.idleFallbackSeconds;
    $("setSegmentDelay").value = r.segmentDelayMs;
    $("setMaxContext").value = r.maxContextMessages;
    $("setMaxMessages").value = r.maxMessagesPerConversation;
    $("setConcurrency").value = r.maxConcurrentReplies;
    $("setProfileLookup").value = r.profileLookupCount;
    $("setProfileLines").value = r.profileSummaryLines;
    $("setProfileChars").value = r.maxProfileChars;
    $("setEnableSummary").checked = r.enableProfileSummary;
    $("setSummaryThreshold").value = r.profileSummaryThreshold;
    $("setSummaryChars").value = r.profileSummaryMaxChars;
    $("setSummaryInterval").value = r.profileSummaryIntervalSeconds;
    $("setSplitReplies").checked = r.splitReplies;
    $("setEnableStickers").checked = r.enableStickers;
    $("setStickerMax").value = r.stickerLibraryMax;
    $("setStickerCandidates").value = r.stickerCandidates;
    $("setStickerCurate").value = r.stickerCurateIntervalSeconds;
    $("setStickerCooldown").value = r.stickerCooldownSeconds;
    $("setEnablePoke").checked = r.enablePoke !== false;
    $("setPokeCooldown").value = r.pokeCooldownSeconds;
    $("setMood").value = r.mood || "";
    $("setMoodTtl").value = r.moodTtlSeconds;
    $("moodHint").textContent = r.moodSummary ? "现在：" + r.moodSummary : "";
    $("setEnableMusic").checked = r.enableMusic !== false;
    $("setMusicSources").value = r.musicSources || "";
    $("setMusicBitrate").value = r.musicBitrate;
    $("setMusicMaxMb").value = r.musicMaxDownloadMb;
    $("setMusicAnalysisSeconds").value = r.musicMaxAnalysisSeconds;
    $("setMusicLibraryMax").value = r.musicLibraryMax;
    $("setMusicNoteTtlDays").value = r.musicNoteTtlDays;
    $("setMusicListenCooldown").value = r.musicListenCooldownSeconds;
    $("setMusicUnderstandModel").value = r.musicUnderstandModel || "";
    $("setMusicSendAudio").checked = r.musicSendAudioToModel !== false;
    $("audioNeteaseBase").value = r.neteaseBaseUrl || "";
    $("setMusicKeepAudio").checked = r.musicKeepAudio === true;
    $("musicHint").textContent = "网易云 Cookie：" + (r.neteaseCookieSet ? "已设置（环境变量）" : "未设置（可选）");
    $("setEnableLinkPreview").checked = r.enableLinkPreview !== false;
    $("setEnableWebSearch").checked = r.enableWebSearch === true;    $("setWebSearchUseModelSearch").checked = r.webSearchUseModelSearch !== false;
    $("setWebSearchSources").value = r.webSearchSources || "";
    $("setWebSearchMaxResults").value = r.webSearchMaxResults;
    $("setWebSearchCooldown").value = r.webSearchCooldownSeconds;
    $("setWebSearchTimeoutSeconds").value = r.webSearchTimeoutSeconds;
    $("setEnableVoice").checked = r.enableVoice === true;
    $("setVoiceName").value = r.voiceName || "";
    $("setVoiceSpeed").value = r.voiceSpeed;
    $("setVoiceMaxChars").value = r.voiceMaxChars;
    $("setTtsServiceUrl").value = r.ttsServiceUrl || "";
    $("setLinkPreviewTimeout").value = r.linkPreviewTimeoutSeconds;
    $("setLinkPreviewMax").value = r.linkPreviewMax;

    // NapCat 状态条（对应桌面版 InfoBar）
    const d = connDisplay();
    const bar = $("napcatBar");
    bar.className = "status-bar " + (d.level === "on" ? "" : "warn");
    $("napcatTitle").textContent = d.level === "on"
      ? "已接入 QQ"
      : d.level === "warn" ? "QQ 账号已离线" : "未连接协议端";
    $("napcatMsg").textContent = d.level === "on"
      ? `协议端在线，账号 QQ ${state.status?.account?.selfId || "-"}。消息通道正常。`
      : d.level === "warn"
        ? "协议端连着，但 QQ 账号登录已失效 —— 消息一条都收不到。回到「聊天」页，顶部卡片里直接扫码就能重新登录。"
        : `无法连接 ${e.oneBotAddress}。请确认 NapCat 容器正在运行且已开启对应的 OneBot 服务。`;
    $("napcatLoginBtn").hidden = d.level === "on";

    // 放在最后：全部回填成功才认为可保存
    state.settingsLoaded = true;
    clearSettingsDirty(); // 刚和服务端对齐，没未保存的修改
  }

  async function saveSettings() {
    // 关键防护：表单没回填完就保存 = 所有字段是空/0，会把白名单清空（→ 忽略全部消息）、
    // 把 AI 关掉、把所有数值压倒最小值 —— 相当于一键把自己的配置全毁掉。
    if (!state.settingsLoaded) {
      toast("设置还没加载成功，请刷新页面后重试");
      return;
    }

    const payload = {
      // 模型接口（面板可改；留空 = 回退环境变量）
      modelBaseUrl: $("setBaseUrl").value.trim(),
      model: $("setModel").value.trim(),
      botPersona: $("setPersona").value,
      messageWhitelist: $("setWhitelist").value,
      aiDesire: Number($("setDesire").value),
      suitabilityThreshold: Number($("setThreshold").value),
      aiModeEnabled: $("setAiMode").checked,
      maxTokens: Number($("setMaxTokens").value),
      groupCooldownSeconds: Number($("setGroupCooldown").value),
      privateCooldownSeconds: Number($("setPrivateCooldown").value),
      idleFallbackSeconds: Number($("setIdleFallback").value),
      splitReplies: $("setSplitReplies").checked,
      segmentDelayMs: Number($("setSegmentDelay").value),
      maxContextMessages: Number($("setMaxContext").value),
      maxMessagesPerConversation: Number($("setMaxMessages").value),
      maxConcurrentReplies: Number($("setConcurrency").value),
      profileLookupCount: Number($("setProfileLookup").value),
      profileSummaryLines: Number($("setProfileLines").value),
      maxProfileChars: Number($("setProfileChars").value),
      enableProfileSummary: $("setEnableSummary").checked,
      profileSummaryThreshold: Number($("setSummaryThreshold").value),
      profileSummaryMaxChars: Number($("setSummaryChars").value),
      profileSummaryIntervalSeconds: Number($("setSummaryInterval").value),
      enableStickers: $("setEnableStickers").checked,
      stickerLibraryMax: Number($("setStickerMax").value),
      stickerCandidates: Number($("setStickerCandidates").value),
      stickerCurateIntervalSeconds: Number($("setStickerCurate").value),
      stickerCooldownSeconds: Number($("setStickerCooldown").value),
      enablePoke: $("setEnablePoke").checked,
      pokeCooldownSeconds: Number($("setPokeCooldown").value),
      moodTtlSeconds: Number($("setMoodTtl").value),
      mood: $("setMood").value.trim(),
      enableMusic: $("setEnableMusic").checked,
      musicSources: $("setMusicSources").value.trim(),
      musicBitrate: Number($("setMusicBitrate").value),
      musicMaxDownloadMb: Number($("setMusicMaxMb").value),
      musicMaxAnalysisSeconds: Number($("setMusicAnalysisSeconds").value),
      musicLibraryMax: Number($("setMusicLibraryMax").value),
      musicNoteTtlDays: Number($("setMusicNoteTtlDays").value),
      musicListenCooldownSeconds: Number($("setMusicListenCooldown").value),
      musicUnderstandModel: $("setMusicUnderstandModel").value.trim(),
      musicSendAudioToModel: $("setMusicSendAudio").checked,
      neteaseBaseUrl: $("audioNeteaseBase").value.trim(),
      musicKeepAudio: $("setMusicKeepAudio").checked,
      enableLinkPreview: $("setEnableLinkPreview").checked,
      enableWebSearch: $("setEnableWebSearch").checked,
      webSearchUseModelSearch: $("setWebSearchUseModelSearch").checked,
      webSearchSources: $("setWebSearchSources").value.trim(),
      webSearchMaxResults: Number($("setWebSearchMaxResults").value),
      webSearchCooldownSeconds: Number($("setWebSearchCooldown").value),
      webSearchTimeoutSeconds: Number($("setWebSearchTimeoutSeconds").value),
      enableVoice: $("setEnableVoice").checked,
      voiceName: $("setVoiceName").value.trim(),
      voiceSpeed: Number($("setVoiceSpeed").value),
      voiceMaxChars: Number($("setVoiceMaxChars").value),
      ttsServiceUrl: $("setTtsServiceUrl").value.trim(),
      linkPreviewTimeoutSeconds: Number($("setLinkPreviewTimeout").value),
      linkPreviewMax: Number($("setLinkPreviewMax").value)
    };

    // 密钥单独处理：输入框留空 = 不改（否则每次保存都会把已存的密钥抹掉）；
    // 想清除要点“清除密钥”按钮（那里有二次确认）。
    const typedKey = $("setApiKey").value.trim();
    if (typedKey) payload.apiKey = typedKey;
    else if (pendingApiKeyClear) payload.apiKey = "";

    const btn = $("saveBtn");
    btn.disabled = true;
    try {
      const data = await api("/api/settings", { method: "POST", body: JSON.stringify(payload) });
      state.aiMode = data.runtime.aiModeEnabled;
      renderAiMode();
      const bar = $("saveBar");
      bar.hidden = false;
      $("saveBarText").textContent = "设置已保存并立即生效（会写入 settings.json，重启不回滚）";
      // 密钥保存/清除后清空输入框（不回显），并把“待清除”标记归位
      $("setApiKey").value = "";
      pendingApiKeyClear = false;
      clearSettingsDirty();
      clearTimeout(bar._t);
      bar._t = setTimeout(() => { bar.hidden = true; }, 4000);
    } catch (err) {
      toast("保存失败：" + err.message);
    } finally {
      btn.disabled = false;
    }
  }

  /* ─────────── 日志 ─────────── */
  function pushLog(text) {
    state.logs.push({ t: Date.now(), text });
    if (state.logs.length > 200) state.logs.splice(0, state.logs.length - 200);
    renderLogs();
  }

  function renderLogs() {
    const box = $("logBox");
    if (!box) return;
    const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 60;
    box.innerHTML = state.logs.map((l) => {
      const d = new Date(l.t);
      const p = (n) => String(n).padStart(2, "0");
      return `<span class="lt">${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}</span> ${esc(l.text)}`;
    }).join("\n");
    if (nearBottom) box.scrollTop = box.scrollHeight;
  }

  /* ─────────── 实时事件（SSE） ─────────── */

  function applyState(data) {
    state.status = data.status;
    state.aiMode = data.aiMode;
    state.conversations = data.conversations || [];
    state.byKey = new Map(state.conversations.map((c) => [c.key, c]));

    if (state.activeKey && !state.byKey.has(state.activeKey)) {
      state.activeKey = null;
      state.messages.clear();
      renderMessages();
    }

    renderConversations();
    renderConn();
    renderAiMode();
    renderThinking();
  }

  function connectEvents() {
    const es = new EventSource(withToken("/api/events"));

    es.addEventListener("state", (e) => applyState(JSON.parse(e.data)));

    es.addEventListener("conversations", (e) => {
      const data = JSON.parse(e.data);
      state.conversations = data.conversations || [];
      state.byKey = new Map(state.conversations.map((c) => [c.key, c]));
      renderConversations();
      renderThinking();
    });

    es.addEventListener("message", (e) => {
      const { key, message } = JSON.parse(e.data);
      const list = state.messages.get(key) || [];
      // 去重：SSE 与 REST 可能重叠
      if (list.some((m) => m.seq === message.seq)) return;
      list.push(message);
      state.messages.set(key, list);

      if (key === state.activeKey) {
        appendMessageNode(message);
        fetch(withToken(`/api/conversations/${encodeURIComponent(key)}/read`), { method: "POST", headers: authHeaders() }).catch(() => {});
      }
      renderConversations();
    });

    es.addEventListener("thinking", (e) => {
      const { key, thinking } = JSON.parse(e.data);
      const c = state.byKey.get(key);
      if (c) c.thinking = thinking;
      renderConversations();
      if (key === state.activeKey) renderThinking();
    });

    es.addEventListener("log", (e) => pushLog(JSON.parse(e.data).text));

    es.onerror = () => {
      $("connText").textContent = "面板连接中断，重连中…";
      $("connDot").className = "dot busy";
    };
  }

  /* ─────────── 启动 ─────────── */

  /// 切页。离开设置页且有未保存的修改时会先问一句 ——
  /// 否则用户会以为“改了自动复原”：其实是从未保存，回页时又被服务端值回填了。
  function showPage(page) {
    if (page !== "settings" && !$("pageSettings").hidden && settingsDirty &&
        !confirm("设置页还有未保存的修改，确定离开吗？")) {
      return false;
    }

    for (const b of document.querySelectorAll(".navitem, .mtab")) {
      if (b.dataset.page === page) b.classList.add("active"); else b.classList.remove("active");
    }

    $("pageChat").hidden = page !== "chat";
    $("pageSettings").hidden = page !== "settings";

    if (page === "settings") {
      loadSettings().catch((e) => toast("加载设置失败：" + e.message));
      // 页面刚显示出来时元素才有尺寸，分节导航的高亮要等这一刻才能算准
      if (refreshSettingsNav) setTimeout(refreshSettingsNav, 0);
    }
    if (page === "chat" && needsLogin()) pollLogin(false);
    if (page === "settings") {
      // 去设置页就把聊天视图收起来：回来时看到的是列表，而不是停在某个会话上
      state.chatOpen = false;
      syncMobileView();
    }
    return true;
  }

  function bindUi() {
    // 导航（桌面：左侧 rail；手机：底部标签栏 —— 共用同一套 data-page）
    for (const btn of document.querySelectorAll(".navitem, .mtab")) {
      btn.addEventListener("click", () => showPage(btn.dataset.page));
    }

    // 手机端：从聊天返回会话列表
    $("chatBack").addEventListener("click", () => {
      state.chatOpen = false;
      syncMobileView();
      renderConversations();
    });

    // 扫码卡片：换一张 / 收起
    $("loginRefresh").addEventListener("click", () => {
      $("loginRefresh").disabled = true;
      state.login.qr = null;
      renderLoginCard();
      pollLogin(true);
    });
    $("loginCollapse").addEventListener("click", () => {
      state.login.collapsed = true;
      renderLoginCard();
    });
    // 设置页的 NapCat 状态条 → 一键回到聊天页看二维码
    $("napcatLoginBtn").addEventListener("click", () => {
      state.login.collapsed = false;
      if (showPage("chat")) pollLogin(true);
      renderLoginCard();
    });

    // 会话列表搜索：用 rAF 节流，避免每次按键都重排整个列表
    let searchRaf = 0;
    $("search").addEventListener("input", (e) => {
      const value = e.target.value;
      cancelAnimationFrame(searchRaf);
      searchRaf = requestAnimationFrame(() => {
        state.search = value;
        renderConversations();
      });
    });

    $("aiToggle").addEventListener("click", async () => {
      try {
        const data = await api("/api/ai-mode", {
          method: "POST",
          body: JSON.stringify({ enabled: !state.aiMode })
        });
        state.aiMode = data.aiMode;
        renderAiMode();
        toast(state.aiMode ? "AI 自动回复已开启" : "AI 自动回复已关闭");
      } catch (e) {
        toast("切换失败：" + e.message);
      }
    });

    const input = $("input");
    input.addEventListener("input", autoGrow);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
      }
    });
    $("sendBtn").addEventListener("click", sendMessage);

    // 主题
    const saved = localStorage.getItem("qqchat.theme") || "system";
    $("setTheme").value = saved;
    applyTheme(saved);
    $("setTheme").addEventListener("change", (e) => {
      localStorage.setItem("qqchat.theme", e.target.value);
      applyTheme(e.target.value);
    });

    $("setDesire").addEventListener("input", (e) => { $("desireVal").textContent = e.target.value; });
    $("setThreshold").addEventListener("input", (e) => { $("threshVal").textContent = e.target.value; });
    $("saveBtn").addEventListener("click", saveSettings);

    // 清除密钥：必须先确认（密钥没了机器人就发不出话，不是小事）
    $("clearApiKey").addEventListener("click", () => {
      if (!state.settingsLoaded) { toast("设置还没加载成功，请刷新页面后重试"); return; }
      pendingApiKeyClear = true;
      $("setApiKey").value = "";
      $("apiKeySrc").textContent = "待清除（点保存后生效）";
      markSettingsDirty();
      toast("点“保存设置”后生效（会回退到环境变量里的密钥）");
    });

    // 任何改动都标记为“未保存”（主题除外：它只存 localStorage，不进保存请求）
    const dirtyOnEdit = (ev) => { if (!ev.target || ev.target.id !== "setTheme") markSettingsDirty(); };
    $("pageSettings").addEventListener("input", dirtyOnEdit);
    $("pageSettings").addEventListener("change", dirtyOnEdit);

    // 右键菜单
    document.addEventListener("click", closeContextMenu);
    // 右键菜单：滚动时关闭。必须 passive + 先判状态，否则每次滚动都写 DOM 造成样式失效
    document.addEventListener("scroll", () => {
      if (ctxKey !== null) closeContextMenu();
    }, { capture: true, passive: true });
    $("ctxMenu").addEventListener("click", async (e) => {
      const act = e.target.dataset.act;
      if (!act || !ctxKey) return;
      const key = ctxKey;
      closeContextMenu();

      if (act === "read") {
        await fetch(withToken(`/api/conversations/${encodeURIComponent(key)}/read`), { method: "POST", headers: authHeaders() }).catch(() => {});
      } else if (act === "delete") {
        if (!confirm("确定删除这个会话？会话记录会一并从磁盘移除。")) return;
        await fetch(withToken(`/api/conversations/${encodeURIComponent(key)}/delete`), { method: "POST", headers: authHeaders() }).catch(() => {});
        if (state.activeKey === key) {
          state.activeKey = null;
          state.messages.delete(key);
          renderMessages();
        }
        toast("会话已删除");
      } else if (act === "profile") {
        const uid = key.split(":")[1];
        if (key.startsWith("private:")) showProfile(uid);
        else toast("群会话没有单一成员档案，点击消息里的 QQ 号查看具体成员");
      } else if (act === "archive") {
        showArchive(key);
      }
    });

    $("profileClose").addEventListener("click", () => { $("profileModal").hidden = true; });
    $("profileModal").addEventListener("click", (e) => {
      if (e.target === $("profileModal")) $("profileModal").hidden = true;
    });

    // 表情包库
    $("openStickerLib").addEventListener("click", () => { openStickerLib(); });
    $("stickerClose").addEventListener("click", () => { $("stickerModal").hidden = true; });
    $("stickerModal").addEventListener("click", (e) => {
      if (e.target === $("stickerModal")) $("stickerModal").hidden = true;
    });
    $("stickerCurate").addEventListener("click", requestStickerCurate);
    $("stickerImport").addEventListener("click", requestStickerImport);
    $("stickerCurateNow").addEventListener("click", requestStickerCurate);
    $("stickerImportAlbum").addEventListener("click", requestStickerImport);

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape") {
        closeContextMenu();
        $("profileModal").hidden = true;
        $("stickerModal").hidden = true;
      }
    });
  }

  function applyTheme(theme) {
    const dark = theme === "dark" ||
      (theme === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.dataset.theme = dark ? "dark" : "light";
  }

  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if ((localStorage.getItem("qqchat.theme") || "system") === "system") applyTheme("system");
  });

  // ── 音频源接入弹窗：自己编辑/添加音源接口，并可以当场试听一首验证 ──
  // 不另存一份配置：弹窗里的字段就是设置页对应字段的“放大版”，
  // 关弹窗/试听前写回去，保存与回填契约始终只有一处（探针在盯这个）。
  function openAudioSources() {
    $("audioSources").value = $("setMusicSources").value;
    $("audioModel").value = $("setMusicUnderstandModel").value;
    $("audioTestOut").textContent = "";
    $("audioModal").hidden = false;
  }

  function writeBackAudioSources() {
    $("setMusicSources").value = $("audioSources").value;
    $("setMusicUnderstandModel").value = $("audioModel").value;
  }

  function bindAudioSources() {
    $("openAudioSources").addEventListener("click", openAudioSources);
    $("audioClose").addEventListener("click", () => { writeBackAudioSources(); $("audioModal").hidden = true; });

    // 一键填预设：公开音源会挂、会改参数，能随手改才是关键
    const presets = [
      ["audioPresetMeting", "meting|https://api.qijieya.cn/meting/?type=url&id={id}&br={br}"],
      ["audioPresetGdstudio", "gdstudio|https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={id}&br={br}&s={crc32}"],
      ["audioPresetDirect", "direct|https://example.com/song/{id}.mp3"]
    ];
    presets.forEach(([id, line]) => {
      $(id).addEventListener("click", () => {
        const box = $("audioSources");
        box.value = (box.value.trim() ? box.value.trim() + "\n" : "") + line;
      });
    });
    $("audioClear").addEventListener("click", () => { $("audioSources").value = ""; });

    $("audioTestGo").addEventListener("click", async () => {
      const song = $("audioTestSong").value.trim();
      if (!song) { toast("先填个歌名"); return; }
      const out = $("audioTestOut");
      out.textContent = "正在试听…（搜歌 → 歌词 → 音频 → 波形分析 → 模型听感，可能要十几秒）";
      try {
        writeBackAudioSources();      // 先把弹窗里的改动写回设置，否则测的还是旧配置
        await saveSettings();
        const r = await api("/api/music/test", { method: "POST", body: JSON.stringify({ song }) });
        out.textContent = r && r.ok ? r.note : "没听到：" + ((r && r.note) || "（无返回）");
      } catch (err) {
        out.textContent = "试听失败：" + err.message;
      }
    });

    $("musicTestNow").addEventListener("click", () => { openAudioSources(); });

    /* ─────────── 语音（TTS）───────────
       两条入口：
         • 「试听一句」把文本交给机器人转发的 TTS，拿回 wav 字节直接在这里播 ——
           当场就能确认“服务通不通、音色像不像、语速合不合适”；
         • 「检查 TTS 服务」只看对方 /health 有没有应答（排障用，不合成）。
       服务地址用的是**已保存**的设置（面板不做“拿任意 URL 去访问”的入口）。 */
    $("voiceTestGo").addEventListener("click", async () => {
      const out = $("voiceTestHint");
      const text = $("voiceTestText").value.trim() || "你好呀，我是昭，这是一条语音测试。";
      out.textContent = "正在合成…（第一次要等几秒）";
      try {
        const res = await fetch(withToken("/api/voice/test"), {
          method: "POST",
          headers: authHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify({
            text,
            voice: $("setVoiceName").value.trim(),
            speed: Number($("setVoiceSpeed").value)
          })
        });
        if (!res.ok) {
          const data = await res.json().catch(() => null);
          out.textContent = "合成失败：" + ((data && data.error) || ("HTTP " + res.status));
          return;
        }
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const kb = Math.round(blob.size / 1024);

        // 合成好的音频挂到页面上（不只是自动播一次）：
        // 浏览器不让自动播放（或没声卡/无头环境）时，还能手动点那个播放器听。
        const holder = $("voiceTestAudio");
        holder.innerHTML = "";
        const audio = document.createElement("audio");
        audio.controls = true;
        audio.src = url;
        audio.style.height = "32px";
        audio.style.marginTop = "6px";
        audio.addEventListener("ended", () => URL.revokeObjectURL(url));
        holder.appendChild(audio);

        try {
          await audio.play();
          out.textContent = `合成成功（${kb} KB）—— 正在播放…`;
        } catch (playErr) {
          out.textContent = `合成成功（${kb} KB）—— 浏览器没让自动播放，点开下面的播放器听：`;
        }
      } catch (err) {
        out.textContent = "试听失败：" + err.message;
      }
    });

    /* ─────────── 联网搜索 ───────────
       一个入口两种用法：填搜索词就是搜，填 http(s) 网址就是读那页正文。
       为什么让面板直接跑：搜索能不能用跟服务器 IP、网关支不支持 google_search 强相关，
       当场跑一次比在群里碰运气强。 */
    $("searchTestGo").addEventListener("click", async () => {
      const out = $("searchTestOut");
      const value = $("searchTestQuery").value.trim();
      if (!value) { toast("先填个搜索词或网址"); return; }
      const isUrl = /^https?:\/\//i.test(value);
      out.textContent = isUrl ? "正在读页面…" : "正在搜索…（模型自带搜索要等几秒）";
      try {
        await saveSettings();   // 先用当前设置，不然测的是旧配置
        const r = await api("/api/search/test", {
          method: "POST",
          body: JSON.stringify(isUrl ? { url: value } : { query: value })
        });
        out.textContent = (r && (r.note || r.text)) || ("没查到：" + ((r && r.error) || "上游无返回"));
      } catch (err) {
        out.textContent = "失败：" + ((err.data && err.data.error) || err.message);
      }
    });

    $("voiceHealthGo").addEventListener("click", async () => {
      const out = $("voiceTestHint");
      out.textContent = "正在问 TTS 服务…";
      try {
        const r = await api("/api/voice/health");
        const voices = (r && r.voices) || [];
        out.textContent = `TTS 正常：${r.url}；当前音色 ${r.currentVoice}；可用 ${voices.join("、") || "(没列出)"}`;
        fillVoiceOptions(voices);
      } catch (err) {
        const detail = (err.data && err.data.error) || err.message;
        const url = err.data && err.data.url ? `（地址 ${err.data.url}）` : "";
        out.textContent = "TTS 不可用：" + detail + url;
      }
    });

    /* 音色候选项以“服务端实际装了的”为准。
       以前写死四个（模拟器里的 onnx 就没装），选到没装的就只能报错。 */
    function fillVoiceOptions(voices) {
      if (!voices || !voices.length) return;
      const list = $("voiceNameOptions");
      if (!list) return;
      const current = $("setVoiceName").value.trim();
      const all = voices.includes(current) || !current ? voices : [current, ...voices];
      list.replaceChildren(...all.map((v) => Object.assign(document.createElement("option"), { value: v })));
    }

    // 点开音色输入框时顺手拉一次“TTS 服务装了哪些音色”（失败就保持原样，不预置静态候选）。
    // 不放在 loadSettings 里：那里是“保存契约”的关键路径，不该加网络请求。
    $("setVoiceName").addEventListener("focus", async () => {
      try {
        const r = await api("/api/voice/health");
        fillVoiceOptions((r && r.voices) || []);
      } catch (err) {
        // 忽略：TTS 没起来时不该阻塞设置页
      }
    });

    // 网易云扫码登录：拿二维码 → 每 2.5 秒问一次状态 → 803 = 成功
    let qrKey = null;
    let qrTimer = null;
    const qrState = (t) => { $("neteaseLoginState").textContent = t; };

    function stopQrPolling() {
      if (qrTimer) { clearInterval(qrTimer); qrTimer = null; }
    }

    async function startNeteaseLogin() {
      stopQrPolling();
      $("neteaseQr").hidden = true;
      qrState("正在获取二维码…");
      try {
        const r = await api("/api/netease/qr", { method: "POST", body: "{}" });
        if (!r || !r.qrimg) {
          qrState("拿不到二维码：" + ((r && r.error) || "（自建接口不可用）"));
          return;
        }

        qrKey = r.key;
        $("neteaseQr").src = r.qrimg;
        $("neteaseQr").hidden = false;
        qrState("请用网易云 App 扫码");

        qrTimer = setInterval(async () => {
          try {
            const s = await api("/api/netease/qr/check", { method: "POST", body: JSON.stringify({ key: qrKey }) });
            const code = s && s.code;
            if (code === 800) { qrState("二维码已过期，重新点“扫码登录”"); stopQrPolling(); $("neteaseQr").hidden = true; }
            else if (code === 801) { qrState("等待扫码…"); }
            else if (code === 802) { qrState("已扫码，请在手机上确认"); }
            else if (code === 803) {
              qrState("✅ 已登录（VIP 歌也能拿地址了）");
              $("neteaseQr").hidden = true;
              stopQrPolling();
              toast("网易云登录成功");
            }
            else if (s && s.error) { qrState("查状态失败：" + s.error); }
          } catch (err) {
            qrState("查状态失败：" + err.message);
          }
        }, 2500);
      } catch (err) {
        qrState("登录请求失败：" + err.message);
      }
    }

    $("neteaseLogin").addEventListener("click", startNeteaseLogin);
  }

  async function boot() {
    bindUi();
    bindAudioSources();
    initSettingsNav();
    renderAiMode();
    renderMessages();
    syncMobileView();   // 刷新后回到列表视图，不要停在某个会话上

    try {
      const data = await api("/api/state");
      applyState(data);
      // 默认打开第一个会话；但手机端不这么做 —— 窄屏是主从式导航，
      // 一上来就进会话会让人失去方向（返回键旁边只剩一个会话名）。
      const narrow = typeof matchMedia === "function" && matchMedia("(max-width: 760px)").matches;
      if (!state.activeKey && !narrow && state.conversations.length > 0) {
        await selectConversation(state.conversations[0].key);
      }
    } catch (e) {
      toast("加载状态失败：" + e.message);
    }

    connectEvents();

    // 面板一登录就把扫码卡片对齐一次（账号没在线时立刻去要一张二维码）
    renderLoginCard();
    startLoginWatch();
    if (needsLogin()) pollLogin(false);
  }

  document.addEventListener("DOMContentLoaded", boot);
})();
