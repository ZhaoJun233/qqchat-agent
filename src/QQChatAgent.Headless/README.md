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

配置要么在**面板里改**（存进 `data/qqchat.db`，见下），要么用环境变量提供 —— **首次启动时**环境变量当作种子写入；
之后面板里改过的项就以面板为准（再改环境变量不再生效，机器人启动日志里会列出来提醒）。
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
| `QQCHAT_API_KEY` / `OPENAI_API_KEY` | — | **必填**。也支持 `QQCHAT_API_KEY_FILE` ；面板里也能填（存库里的 `secrets` 表，库文件权限 600）；面板填过之后以面板为准 |
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
| `QQCHAT_SPLIT_REPLIES` | `1` | 长回复按句分句发送（最多 4 段，不丢字；不会在小数 / 域名 / 连续标点 / 收尾引号处切断） |
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
| `QQCHAT_ENABLE_VOICE` | `0` | 语音消息总开关（模型填 `speak` 字段时才发）——需要先跑起 `tts` 旁路容器 |
| `QQCHAT_VOICE` | `zh_CN-huayan-medium` | 音色（= `tts/<name>.onnx`）：`huayan-medium/x_low`、`xiao_ya-medium`、`chaowen-medium` |
| `QQCHAT_VOICE_SPEED` | `100` | 语速百分比（100 = 原速，越大越快） |
| `QQCHAT_VOICE_MAX_CHARS` | `80` | 单条语音字数上限（超过就不发语音，改打字） |
| `QQCHAT_TTS_URL` | `http://tts:5000` | TTS 旁路服务地址（机器人拼 `/speak?text=…`，NapCat 去下载） |
| `QQCHAT_WEB_SEARCH` | `1` | 联网搜索总开关（模型填 `search` / `read` 时才用） |
| `QQCHAT_SEARCH_USE_MODEL` | `1` | 优先用“模型自带搜索”（检索在服务商侧完成，结果带来源）；置 `0` 就只用下面的搜索源模板 |
| `QQCHAT_SEARCH_SOURCES` | Wikipedia API | 兑底搜索源模板（每行 `name|url`，`{q}` 是查询词；`searx*`/`wiki*` 有专用解析） |
| `QQCHAT_SEARCH_MAX_RESULTS` | `5` | 每次给模型看几条结果 |
| `QQCHAT_SEARCH_COOLDOWN` | `30` | 同一会话两次联网搜索的最小间隔（秒）；`0` = 不限 |
| `QQCHAT_SEARCH_TIMEOUT` | `20` | 搜索 / 读页面超时（秒） |
| `QQCHAT_SEARCH_READ_CHARS` | `1800` | `read` 抓到的正文截断长度（字） |
| `TZ` | `Asia/Shanghai` | 影响消息时间戳与模型看到的"现在几点" |

> 面板内扫码登录为什么需要令牌：机器人是向 NapCat WebUI 的公开接口
> （`/api/auth/login` + `/api/QQLogin/GetQQLoginQrcode`）取二维码，认证方式与 NapCat 自己的前端一致，
> 不需要额外改 NapCat 配置。不填令牌只是面板里看不到二维码，不影响消息通道。

---

## 数据目录（`/data`）

| 路径 | 内容 |
| --- | --- |
| `data/qqchat.db` | **SQLite 库**：设置、会话、消息（含归档）、人物档案与画像、心情、听过的歌、表情包索引、密钥。WAL 模式，随同 `-wal/-shm` |
| `data/legacy-json/` | 老版本 JSON 的留档（首次启动自动导入库之后移到这里，**不删**） |
| `stickers/*.png` | **表情包图片本体**（索引在库里；二进制不适合塞库，备份/预览/清理都麻烦） |
| `logs/qqchat.log` | 运行日志 |

> 为什么从“一堆 JSON”换成 SQLite：① 会话/消息/档案分散在多个文件里，一次崩溃可能只写了一半，跨文件没法用事务；
> ② 消息是追加型数据，JSON 每次全量重写（几千条就明显卡）；③ 面板要的“某群更早的发言 / 某人某群的画像 / 归档翻旧账”
> 在 JSON 上只能全量读进内存再过滤，SQL 一句就能干。
>
> 备份：直接拷 `data/qqchat.db`（连同 `-wal/-shm`）就完事；想用 SQL 查配置可以 `json_extract(json,'$.AiDesire')`。
> 密钥（面板里填过的 API Key）存在 `secrets` 表里，所以**库文件权限是 600**（不跟 `settings` 混在一起，依然不会跟着配置一块被贴出去）。

JSON 在库里一律不转义中文，`sqlite3 ... "SELECT text FROM messages LIMIT 3"` 直接看得懂。

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

## 语音消息（可选：模型偶尔用声音说一句）
```
模型输出 JSON 里带 speak（要说出口的话；也可以是 true = 把 reply 用语音说）
   └─→ 机器人拼出 http://tts:5000/speak?text=…&voice=…&speed=…
         └─→ 发 OneBot record 段，data.file 就是这个 URL
               └─→ NapCat 自己下载 → 转 silk（native 转换器）→ 上传成语音
机器人这边：不下载音频、不碰 silk、不把音频塞进 WebSocket
```

为什么这么设计（都是踩过的坑）：

- **音频编码交给协议端**：silk 是 QQ 私有格式，自己接编码器版本很容易对不上；
  NapCat 内置了 native 转换器（`convertToNTSilkTct`），且它能直接吃 `http(s)://` / `base64://` / 本地路径。
- **代价是网络可达**：NapCat 必须能访问这个 URL。两个容器同在 `qqchat-net` 时就是 `http://tts:5000`；
  若把 NapCat 放到别的机器上，要用宿主机 IP / 域名，并且别把 `tts` 只绑在 `127.0.0.1`。
- **语音要克制**：提示词反复要求“偶尔用”（道谢/撒娇/唱歌/情绪重的时候），
  代码侧再加一道**同会话 45 秒**的闸门（`VoiceMinIntervalSeconds`）——
  模型不听话也刷不了屏，而且 Piper 是 CPU 串行推理，一条要几秒。
- **一律可降级**（任一环节失败都退化成打字，内容不丢）：开关关闭、超过 `VoiceMaxChars`、
  频率门、`QQCHAT_TTS_URL` 没配、TTS 容器挂了、协议端 retcode≠0（如不支持 record 段）。
- **落库记的是 `[语音] 说的内容`**：模型下一轮才知道“我刚才是用声音说的什么”，不会当自己没说过。

面板「设置 → 语音消息」可以：开关、音色（datalist 给出 4 个中文音色）、语速、字数上限、TTS 地址，
以及**试听一句**（真去 `/speak` 拿回 wav 在浏览器里播，音色/语速可以当场改当场听）和
**检查 TTS 服务**（问对方 `/health`，列出可用音色）。对应接口：`POST /api/voice/test`、`GET /api/voice/health`。

> 音色就是模型文件：换音色 = 往 `/opt/qqchat/tts/` 放一个 `<name>.onnx`（+ 同名 `.json`）。
> 要复现**某个真人的嗓音**需要先把声音用在自己身上得到授权，再单独训练或调用云端克隆 API ——
> 没授权的真人音色不要做。

## 撤回消息与联网搜索

**撤回（`group_recall` / `friend_recall`）** —— 撤回是没有正文的事件，只能靠 notice 同步：

```
群友撤回一条消息
  └─→ 会话里那条变成 [已撤回] 原内容（内容保留：机器人当时在场，直接抹掉会让它“失忆”）
        ├─→ 不再给 (#id)，也不可能被选为 replyTo（即使模型硬填也会被代码拒掉）
        ├─→ 给模型一次开口机会（“撤回了啥”），同会话 90s 冷却
        ├─→ **手误更正不点评**：撤回后同一个人又发了新消息 → 只标记、不给开口机会
        └─→ 提示词明确：可以记得，但不要复述/引用/说“我都看见了”这类话
面板：那条消息会被划掉，并注明“模型看到的是「[已撤回] 原文」”
```

**联网搜索（模型填 `search` / `read`）** —— 两轮动作，和“听音乐”同一套思路：

```
模型输出 {"reply":"我去查一下","search":"关键词"}  或  {"read":"https://…"}
  └─→ 后台真去查/去读（不阻塞本轮回复）
        ├─→ 首选：原生 /v1beta/models/<model>:generateContent + tools:[{google_search:{}}]
        │        —— Google 真去搜，答案带 groundingMetadata（检索词 + 来源标题）
        └─→ 兑底：WebSearchSources 模板（searx* = SearxNG JSON / wiki* = MediaWiki JSON / 其余抽 HTML 链接）
              └─→ 结果作为“刚查到的资料”进**下一轮**提示词，模型拿着事实再说
冷共：同一会话 30 秒只查一次；搜不到/读不到就如实告诉模型“没查到”（别让它编）
SSRF：所有出站 URL（含 read 的）都过 SafeUrl；搜索源地址也在其中
```

为什么默认是“模型自带搜索”而不是爬网页：很多部署环境的出口 IP 是机房地址，
Google/Bing/DuckDuckGo/百度 对爬虫一律回看板页或验证码；而模型订阅本身就能搜 ——
不额外要密钥、不爬虫、结果还带来源。搜索源模板留给“自建 SearxNG / 内网检索 / 干净出口 IP”的场景。

相关接口：`POST /api/search/test`（`{query}` 搜 / `{url}` 读页面）、`POST /api/voice/test`、`GET /api/voice/health`。

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
