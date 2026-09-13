# QQ Chat Agent（Headless / 容器版）

把桌面版（WinUI 3）的 Agent 逻辑抽成**无界面、跨平台、可容器部署**的常驻服务：
接 OneBot v11 协议端（NapCat 等），用 OpenAI 兼容模型自动回复 QQ 私聊与群聊。

```
QQ 客户端 (Linux)  ←被注入—  NapCat (容器)  ←—OneBot v11 WS——  QQ Chat Agent (容器)  ——→  模型 API
```

- **无 UI、无 Windows 依赖**：`net8.0`，Docker 镜像约 80MB（运行阶段基于 `dotnet/runtime:8.0`）
- **非 root 运行**，数据全部落在 `/data` 卷里，支持优雅退出（SIGTERM / `docker stop`）
- **自带健康检查**：`/healthz`（存活）、`/readyz`（是否连上协议端）、`/status`（运行状态 JSON）

---

## 快速开始（Docker Compose）

```bash
# 1. 准备配置
cp .env.example .env
vim .env                      # 至少填 MODEL_API_KEY 和 WHITELIST

# 2. 构建 + 启动
docker compose up -d

# 3. 首次登录：扫二维码
#    推荐：打开机器人面板（http://<主机>:8080/），没登录时顶部会自动出现登录二维码
#    备用：看 NapCat 日志里的 ASCII 二维码
docker compose logs -f napcat
```

扫码登录后，还要让 NapCat 把消息转发出来（**一次性配置**）：

1. 浏览器打开 `http://<主机IP>:6099` → NapCat WebUI
2. **网络配置 → 添加「WebSocket 客户端」（正向 WS）**
   - URL：`ws://qqchat:3001`
   - Token：与 `.env` 里的 `ONEBOT_TOKEN` 保持一致（留空则都不用填）

   > 容器之间用服务名互相解析，所以这里填 `qqchat`（compose 里的服务名），不是 `127.0.0.1`。
3. 回到宿主机验证：

```bash
curl -s http://127.0.0.1:8080/readyz       # {"ready":true,...} 就通了
curl -s http://127.0.0.1:8080/status       # 运行状态详情
```

之后往白名单里的群发一条 `@机器人 你好` 即可。

---

## 环境变量

配置要么写进 `settings.json`（见下），要么用环境变量覆盖 —— **环境变量优先**。
带 `_FILE` 后缀的变量从文件读取内容，适合 Docker secrets：

```yaml
environment:
  QQCHAT_API_KEY_FILE: /run/secrets/model_key
secrets:
  model_key:
    file: ./model_key.txt
```

### 模型（OpenAI 兼容）

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `QQCHAT_API_KEY` / `OPENAI_API_KEY` | — | **必填**。也支持 `QQCHAT_API_KEY_FILE` ；面板里也能填（存 `data/secrets.json`，权限 600，不进 settings.json）；面板填过之后以面板为准 |
| `QQCHAT_BASE_URL` / `OPENAI_BASE_URL` | `https://api.openai.com/v1` | 接口地址（DeepSeek / 通义 / Ollama / 中转…）；面板「Agent 大脑」里可改，改完立即生效；面板改过之后环境变量不再覆盖它 |
| `QQCHAT_MODEL` / `OPENAI_MODEL` | `gpt-4o-mini` | 模型名；面板里可改 |
| `QQCHAT_MAX_TOKENS` | `2048` | 单次回复上限 |

### QQ 通道

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` | `ForwardWebSocket` / `ReverseWebSocket` / `Http` |
| `QQCHAT_ONEBOT_URL` | `ws://127.0.0.1:3001` | 正向：`ws://napcat:3001`；反向：`http://0.0.0.0:3001`；HTTP：`http://napcat:3000` |
| `QQCHAT_ONEBOT_TOKEN` | 空 | 协议端 Access Token |
| `QQCHAT_UIN` | 空 | 机器人 QQ 号（用于识别 `@机器人`；留空则连上后自动获取） |

### 行为

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `QQCHAT_WHITELIST` | 空 | ⚠️ **空 = 严格模式，忽略所有消息**。填群号/QQ 号（逗号或换行分隔），或 `*` 接收全部 |
| `QQCHAT_PERSONA` | 空 | 机器人人设（性格 / 说话风格），每次请求注入 |
| `QQCHAT_AI_DESIRE` | `50` | 对话欲望 0-100，越高越主动插话 |
| `QQCHAT_SUITABILITY_THRESHOLD` | `10` | 发言适合度阈值，低于此值则沉默 |
| `QQCHAT_AI_MODE` | `1` | `0` = 只收消息不回复（调试用） |

### 回复节奏

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `QQCHAT_GROUP_COOLDOWN` | `8` | 同一群的最小回复间隔（秒） |
| `QQCHAT_PRIVATE_COOLDOWN` | `3` | 同一私聊的最小回复间隔（秒） |
| `QQCHAT_IDLE_FALLBACK` | `60` | 静默兜底：超过该秒数没有主动请求时补判断一次；`0` 关闭 |
| `QQCHAT_SPLIT_REPLIES` | `1` | 长回复按句分句发送（最多 4 段，不丢字） |
| `QQCHAT_SEGMENT_DELAY_MS` | `700` | 分句之间的间隔 |
| `QQCHAT_MAX_CONTEXT` | `200` | 喂给模型的最大上下文条数 |
| `QQCHAT_PROFILE_LOOKUP` | `8` | 附带的人物档案数量上限 |

### 运维

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `QQCHAT_DATA_DIR` | `/data`（镜像内） | 数据目录，建议挂卷 |
| `QQCHAT_HEALTH_PORT` | `8080` | 健康检查端口，`0` 关闭 |
| `QQCHAT_HEALTH_BIND` | `+` | 绑定地址；Windows 非管理员下自动回退 `127.0.0.1` |
| `QQCHAT_VERBOSE` | `1` | `0` = 只输出关键日志 |
| `QQCHAT_LOG_FILE` | `1` | `0` = 不写日志文件，只走 stdout |
| `QQCHAT_NAPCAT_WEBUI_URL` | `http://napcat:6099` | NapCat WebUI 地址（面板内扫码登录用） |
| `QQCHAT_NAPCAT_WEBUI_TOKEN` | 空 | NapCat WebUI 令牌（`napcat/config/webui.json` 的 `token`）。填了之后，账号未登录时面板会直接显示登录二维码 |
| `QQCHAT_STICKERS` | `1` | 表情包总开关（自动收集 + 按语境发送 + 自巡检） |
| `QQCHAT_STICKER_MAX` | `120` | 表情包库存储上限（张） |
| `QQCHAT_STICKER_CANDIDATES` | `6` | 每次给模型看的候选张数 |
| `QQCHAT_STICKER_CURATE_INTERVAL` | `3600` | 自巡检间隔（秒），0 = 关 |
| `QQCHAT_STICKER_COOLDOWN` | `120` | 同一会话两次发表情包的最小间隔（秒），0 = 不限 |
| `QQCHAT_ENABLE_POKE` | `1` | 戳一戳总开关：被戳时按语境回话/戳回去（别人互戳只进上下文，不插话） |
| `QQCHAT_POKE_COOLDOWN` | `45` | 戳一戳冷却（秒）：同一个人连着戳只回一次；也是主动戳人的最小间隔 |
| `QQCHAT_MOOD_TTL` | `7200` | 模型写的心情保留多久（秒）：超时没更新就回落到“按被戳次数自动描述”，0 = 不过期 |
| `QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS` | `0` | `1` = 允许从内网/回环地址下载图片。**仅供自建/测试**，公网部署不要开 |
| `TZ` | `Asia/Shanghai` | 影响消息时间戳与模型看到的"现在几点" |

> 面板内扫码登录为什么需要令牌：机器人是向 NapCat WebUI 的公开接口
> （`/api/auth/login` + `/api/QQLogin/GetQQLoginQrcode`）取二维码，认证方式与 NapCat 自己的前端一致，
> 不需要额外改 NapCat 配置。不填令牌只是面板里看不到二维码，不影响消息通道。

---

## 数据目录（`/data`）

| 路径 | 内容 |
| --- | --- |
| `data/settings.json` | 行为配置（面板设置页的唯一所有者） |
| `data/conversations.json` | 会话与消息（超出上限的旧消息归档到 `data/archive/*.jsonl`） |
| `data/member_profiles/*.json` | 人物档案与长期画像 |
| `stickers/index.json` + `stickers/*.png` | **表情包库**（全库共用一份；与 `data/` 并列，方便单独备份/清理） |
| `logs/qqchat.log` | 运行日志 |

> 表情包为什么单独放一层：它是二进制大对象，和 JSON 混在一个目录里既不好备份也不好排查；
> `stickers/` 整个目录直接删除就能重置表情包（下次启动会重建索引）。

JSON 均为 UTF-8 不转义中文，`cat` 就能直接看。

---

## 不用 Compose 的裸 docker 命令

```bash
docker build -f src/QQChatAgent.Headless/Dockerfile -t qqchat-agent .

docker run -d --name qqchat --restart unless-stopped \
  -e QQCHAT_API_KEY=sk-xxxx \
  -e QQCHAT_BASE_URL=https://api.deepseek.com/v1 \
  -e QQCHAT_MODEL=deepseek-chat \
  -e QQCHAT_ONEBOT_URL=ws://172.17.0.1:3001 \
  -e QQCHAT_ONEBOT_TOKEN=your-token \
  -e QQCHAT_WHITELIST=123456789 \
  -e QQCHAT_UIN=10001 \
  -e QQCHAT_PERSONA='你是群里的老群友，说话简短口语化。' \
  -v ./data:/data \
  -p 127.0.0.1:8080:8080 \
  qqchat-agent
```

> `--network host` 时 `QQCHAT_ONEBOT_URL` 可以直接写 `ws://127.0.0.1:3001`；
> 用默认 bridge 网络时用 `host.docker.internal`（Docker Desktop/macOS）或宿主机内网 IP（Linux）。

---

## 不用 Docker（裸跑 .NET）

```bash
dotnet build src/QQChatAgent.Headless/QQChatAgent.Headless.csproj -c Release

# 看全部选项
dotnet run --project src/QQChatAgent.Headless -- --help

# 跑起来
QQCHAT_API_KEY=sk-xxxx \
QQCHAT_ONEBOT_URL=ws://127.0.0.1:3001 \
QQCHAT_WHITELIST=123456789 \
dotnet run --project src/QQChatAgent.Headless
```

反向 WS 模式典型用法（本程序监听 3001，NapCat 主动连入）：

```bash
QQCHAT_ONEBOT_PROTOCOL=ReverseWebSocket \
QQCHAT_ONEBOT_URL=http://0.0.0.0:3001 \
QQCHAT_ONEBOT_TOKEN=your-token \
dotnet run --project src/QQChatAgent.Headless
```

---

## 健康检查与可观测性

```bash
curl -i http://127.0.0.1:8080/healthz    # 200：进程存活
curl -i http://127.0.0.1:8080/readyz     # 200：已连上协议端；503：未连接
curl -s  http://127.0.0.1:8080/status    # 状态快照
curl -s  http://127.0.0.1:8080/api/qqlogin   # 当前登录二维码（面板里的扫码卡片用）
```

`/status` 样例：

```json
{
  "status": "running",
  "uptimeSeconds": 3721,
  "onebot": { "connected": true, "protocol": "ForwardWebSocket", "address": "ws://napcat:3001" },
  "account": { "uin": "10001", "selfId": 10001, "online": true },
  "login": { "qrAvailable": true, "napcatWebUi": "http://napcat:6099" },
  "agent": { "enabled": true, "model": "deepseek-chat", "desire": 50, "personaConfigured": true },
  "conversations": 7
}
```

镜像内置 `HEALTHCHECK`（调用 `--health`，不依赖 curl），所以 `docker ps` 能直接看到 `healthy`：

```bash
docker inspect --format '{{.State.Health.Status}}' qqchat-bot
```

---

## 手机端

面板是响应式的，用手机浏览器打开就能用（≤ 760px 自动切换布局）：

| 桌面 | 手机 |
| --- | --- |
| 左侧导航 rail | 底部标签栏（拇指能碰到的位置，带 iOS 安全区内边距） |
| 会话列表 + 聊天区并排 | 主从式：先列表，点进会话才看聊天，头部有返回键 |
| 右键菜单 | 长按 450ms 弹同一张菜单（iOS 不触发 contextmenu） |
| 输入框 14px | 16px（小于 16px 时 iOS Safari 聚焦会自动放大页面） |
| 扫码卡片横排 | 竖排、二维码按 `min(58vw, 26vh, 200px)` 自适应（矮屏也不会把输入框挤出屏幕） |

细节：`viewport-fit=cover` + `env(safe-area-inset-bottom)` 处理刘海/手势条，
`body { height: 100dvh }` 防止地址栏收起时输入框被切掉，长文本 `word-break` 不横向溢出。
桌面布局完全不变。

> 改这块时注意两个容易踩的点：窄屏下**不要自动打开第一个会话**
> （一上来就进会话会让人失去方向），以及**不要靠 JS 读 `classList` 决定布局** ——
> 布局只看 CSS 媒体查询，JS 只负责 `body.m-chat-open` 这一个视图状态。

## 表情包（全库共用一份）

它是一条完整链路，每个环节坏了都只会“静默地不好用”，所以下面写清每段到底做了什么：

```
群友发图 ──下载─→ 按内容 sha256 去重存盘（stickers/）
                    └─→ 排队让模型看图，生成「一句话说明 + 情绪/场景关键词」
回复前    ──用最近几条对话当检索词，从库里挑 N 张候选（默认 6）塞进提示词
              └─→ 模型在 JSON 里加 sticker 字段挑选；只发图也行（reply 留空）
                      └─→ 机器人读文件 → base64 段 → 发图（可带引用），并记一次使用
容量      ──超出上限按“用得少 ×3 − 闲置天数”淘汰（常用的留着，新增的不会立刻被删）
审核      ──入库时模型同时判定“是不是表情包”：聊天截图/广告/纯文字图 → 直接丢掉，不当候选
频率门    ──同一会话两次发表情包至少隔 StickerCooldownSeconds（默认 120），
              同一张 10 分钟内不重复（库小时模型会“每句都挂同一张”，群里会开愤）
巡检      ──每 N 秒把库（id | 说明 | 用过几次 | 多久前加的）交给模型，
              让它自己决定删哪些；一次最多删 1/5，24 小时内用过的代码侧直接拦下
```

面板「设置 → 表情包」可以改开关 / 上限 / 候选数 / 巡检间隔，并能打开库看缩略图、
手动删除、立即巡检、从登录账号的 QQ 收藏表情导入（NapCat 的 `fetch_custom_face`；
收藏夹为空时会明确告诉你，不会当成错误）。

相关接口：`GET /api/stickers`、`GET /api/stickers/{id}/img`、`POST /api/stickers/{id}/delete`、
`POST /api/stickers/curate`、`POST /api/stickers/import`。

> 两个容易踩的点：图片 URL 来自 QQ 事件，属不可信输入，下载默认拦回环/私有 IP（SSRF）；
> 模型给的是“说明 + 关键词”而不是图片本身，候选必须是检索出来的几张，不能把整库塞进提示词。

## 与桌面版的差异

复用的部分（代码结构与语义完全一致）：OneBot 网关与三种传输、人物档案库、会话持久化、
模型客户端（含多模态识图）、白名单、冷却限流、请求串行队列、静默兜底、回复引用规则。
测试套件验证了这些行为，见下节。

| 差异 | 说明 |
| --- | --- |
| 无界面 | AI 开关改由 `QQCHAT_AI_MODE` 控制 |
| 不再管理 NapCat | 桌面版会下载/启动/配置 NapCat；headless 版只管连，协议端自己跑 |
| 群历史拉取时机 | 桌面版"点开会话时"；headless 版"首次收到该群消息时"（无会话列表可点） |
| **新增** 分句发送 | 桌面版 README 声称有、代码里没有；headless 版真正实现了 |
| **新增** 落盘保留 `SenderId` / `ImageUrls` / `QqMessageId` | 桌面版不存这三个字段，重启后人物档案与识图会失效 |
| **新增** 通配白名单 `*`、健康检查、环境变量/密钥文件配置、JSON 中文不转义 | 面向容器部署 |

---

## 测试

两层验证，都是真实端到端（非 mock 桩）：

```bash
# ① 本地集成测试：起真实机器人进程 + 假协议端（真 WebSocket）+ 假模型（真 HTTP）
dotnet run --project tests/QQChatAgent.IntegrationHarness -c Release

# ② 容器测试：验证真正跑在容器里的机器人（在容器网络里放置假协议端/假模型）
docker network create qqchat-e2e
docker build -f tests/QQChatAgent.IntegrationHarness/Dockerfile -t qqchat-harness .
docker run -d --name harness --hostname harness --network qqchat-e2e \
  -e HARNESS_BOT_HEALTH_URL=http://bot:8080 qqchat-harness --serve-external
docker run -d --name bot --hostname bot --network qqchat-e2e \
  -e QQCHAT_API_KEY=sk-mock \
  -e QQCHAT_BASE_URL=http://harness:18899/v1 \
  -e QQCHAT_ONEBOT_URL=ws://harness:13099 \
  -e QQCHAT_WHITELIST=12321 \
  qqchat-agent
docker logs harness      # 断言结果
```

覆盖：反向/正向 WebSocket 握手、登录号识别、群/私聊链路、白名单严格模式与 `*` 通配、
模型沉默时不发言、回复引用（被插话时才带引用）、分句发送不丢字、人物档案读写、
提示词组装（人设 / 档案 / `{发送者}{内容--时间}` 格式）、重启后会话恢复、健康端点。

---

## 故障排查

| 现象 | 原因与处理 |
| --- | --- |
| `/readyz` 一直 503 | 查 `docker compose logs qqchat`：地址/Token 不对，或 NapCat 没开正向 WS |
| 日志出现「白名单为空 → 忽略所有消息」 | `QQCHAT_WHITELIST` 没填。填 `*` 或具体群号 |
| `/status` 里 `connected:true` 但机器人不说话 | 模型把 `reply` 留空了（这是设计行为，避免刷屏）。调高 `QQCHAT_AI_DESIRE`，或在人设里写清楚什么时候该接话 |
| 健康检查端口起不来 | Windows 非管理员绑不了 `+`：设 `QQCHAT_HEALTH_BIND=127.0.0.1`，或 `QQCHAT_HEALTH_PORT=0` 关闭 |
| 收不到语音/文件 | Docker 版 NapCat 的已知限制（文字与图片正常） |
| 时间戳差 8 小时 | 设 `TZ=Asia/Shanghai` 并重启容器 |
| 登录态丢失、每次都要扫码 | `./napcat/ntqq` 卷没挂上或属主不对（`NAPCAT_UID`/`NAPCAT_GID`） |

## 许可

与桌面版同为 MIT。NapCat 遵循其自有许可（非商业），另见仓库根 `README.md`。
