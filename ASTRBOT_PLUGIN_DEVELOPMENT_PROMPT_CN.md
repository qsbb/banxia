# AstrBot Quest 角色桥接插件开发提示词

把以下整段提示词交给负责 AstrBot 后端开发的编码助手。目标插件暂定名为 `astrbot_plugin_quest_avatar_bridge`。

---

你是负责 AstrBot 插件的高级 Python 工程师。请在现有 AstrBot 工作区中开发一个新插件 `astrbot_plugin_quest_avatar_bridge`，为 Meta Quest/Unity MMD 角色前端提供实时对话与角色行为决策服务。

## 一、先做只读调查

1. 先阅读目标 AstrBot 安装版本、新版官方插件文档、本地 `plugin/CONVENTIONS.md`，以及 `astrbot_plugin_conversation_flow`、`astrbot_plugin_relationship`、`astrbot_plugin_voice_hub` 已公开的契约。
2. 输出入口、依赖、配置、运行生命周期和跨插件边界，确认后再写代码。
3. 不使用旧版插件指南中已废弃的接口；不得臆造 `register_websocket` 等未在目标版本确认存在的 API。
4. 不修改 AstrBot Core、现有业务插件、service hub 或 orchestration hub。确需跨插件协作时，只使用双方明确声明且版本兼容的契约；禁止 `hasattr` 式 duck typing。

## 二、产品目标和硬边界

- Unity 只负责传感和表现：上报说话、触碰、手势和打断事实；播放音频；执行受约束的语义意图。
- AstrBot 插件负责全部角色决策：说什么、是否回应、接受或拒绝触碰、情绪、动作、注视目标、交互后的恢复反应。
- Unity 不得根据 `head_pat`、`handshake`、`cheek_pinch` 自行推断开心、害羞或躲避。
- 后端只返回模型无关的语义意图，不返回 PMX 骨骼名、Morph 名、动画文件路径或 Unity 对象名。
- 第一版先支持文字输入、流式文字、结构化动作意图和可打断流程；STT/TTS 使用适配器，缺少服务时允许明确降级，不能伪造成功。

## 三、使用当前官方插件 API

- 插件入口 `main.py`，插件类继承 `Star`，构造函数接收 `Context` 和 `AstrBotConfig`。
- 用 `metadata.yaml` 声明元数据和经核对后的 `astrbot_version`；版本只以 `metadata.yaml` 为事实源。
- 用 `_conf_schema.json` 声明配置。
- 首版优先使用 `context.register_web_api()` 注册 HTTP 接口；handler 使用 `astrbot.api.web.request`、`json_response()`、`error_response()`、`stream_response()`。
- 下行实时事件使用 SSE；上行使用异步 HTTP。不要为了 WebSocket 修改 Core。若目标版本确有公开稳定的 WebSocket 扩展 API，可把它作为后续可替换 Transport。
- LLM 调用使用目标版本公开的 `context.llm_generate()`。有 AstrBot UMO 时可用 `get_current_chat_provider_id(umo)`；Quest 独立会话没有 UMO 时，从配置读取明确的 `chat_provider_id`，缺失则返回可诊断错误。
- 持久化小数据使用插件 KV；较大数据放到 AstrBot `data/plugin_data/<plugin_name>/`，不得写插件目录。
- 所有网络和流式操作必须异步；禁止 `requests`。`terminate()` 必须取消任务、关闭 SSE、释放队列和网络资源。

## 四、通信接口

在 `/{PLUGIN_NAME}/` 前缀下注册：

- `POST session/start`：创建会话，返回 `session_id`、协议版本和能力列表。
- `GET events/<session_id>`：SSE 下行通道。
- `POST turn/start`：启动文字轮次；请求含 `session_id`、`turn_id`、`text`。
- `POST audio/chunk`：预留 PCM16 单声道上行；第一版可返回 `not_configured`，不可静默丢弃。
- `POST audio/end`：结束输入音频。
- `POST interaction`：上报交互事实。
- `POST interrupt`：取消指定轮次的 LLM、TTS 和待发送事件。
- `POST session/close`：清理会话。
- `GET health`：只返回状态、检查项、原因和协议版本，不泄露配置、文本、用户信息或令牌。

所有请求做 schema 校验、大小限制、速率限制、会话所有权校验和认证。外部 Unity 客户端如何访问 Dashboard 扩展路由及携带认证，必须根据目标版本实测并记录，不能照搬 Page bridge 的隐式认证。

## 五、事件协议

SSE 的每条 `data` 都是一个 JSON 对象，至少含 `protocol_version`、`session_id`、`event_id`、`type`、`timestamp_ms`；轮次事件还含 `turn_id`。

上行示例：

```json
{"type":"interaction","session_id":"s1","event_id":"e9","name":"head_pat","phase":"start","strength":0.7,"duration_ms":0,"hand":"right"}
{"type":"interrupt","session_id":"s1","turn_id":"t3"}
```

下行事件类型：`asr.partial`、`asr.final`、`reply.text.delta`、`reply.audio.chunk`、`avatar.intent`、`reply.end`、`error`。

`avatar.intent` 第一版固定为扁平且易被 Unity 解析的结构：

```json
{
  "type":"avatar.intent",
  "session_id":"s1",
  "turn_id":"t3",
  "in_reply_to_event_id":"e9",
  "emotion":"shy",
  "gesture":"step_back",
  "look_at":"away",
  "intensity":0.65,
  "duration_ms":1800,
  "reason_code":"boundary_soft_refusal"
}
```

首版白名单：

- `emotion`: `neutral|happy|shy|surprised|concerned|uncomfortable`
- `gesture`: `idle|talk|wave|bow|handshake|head_pat|cheek_pinch|refuse|step_back`
- `look_at`: `user|hand|away|none`

未知值必须拒绝或降级为 `neutral/idle/none`，不能把任意字符串传给 Unity。`reason_code` 是程序诊断码，不包含思维链，也不发送给模型显示层。

## 六、行为决策流程

1. `InteractionEvent` 只是事实，先校验和去抖，不立刻生成固定反应。
2. 从当前会话、角色设定以及明确可用的关系快照组装最少必要上下文；动态内容不要每轮拼接进稳定 system prompt。
3. 要求 LLM 返回“回复文本 + 结构化角色意图”，用 Pydantic/JSON Schema 严格校验。
4. 解析失败时最多做一次结构修复；仍失败则返回安全的 `neutral/idle/none` 和普通文本，记录不含用户正文的错误码。
5. 对触碰可选择接受、拒绝、害羞、回避、口头回应或不回应；不得把用户触碰类型机械映射为固定情绪。
6. 所有意图再经过确定性的 `InteractionPolicy`：白名单、强度范围、冷却时间、连续触碰、取消状态和安全边界。
7. 对话中注视也必须通过 `look_at` 下发；嘴型振幅同步由 Unity 根据实际音频完成。

## 七、并发和取消

- 每个 `session_id` 一个有界事件队列，每个 `turn_id` 一个可取消任务组。
- 新轮次可取消旧轮次；`interrupt` 后不得再发送旧轮次的文字、音频、动作或 `reply.end`。
- 对每个异步阶段在发送前再次检查 generation/turn token，防止迟到回调污染新轮次。
- SSE 慢客户端不能拖死系统：可合并或丢弃 `asr.partial`，但不能丢 `avatar.intent`、`reply.end`、`error`。
- TTS 输出约定 PCM16、单声道、24000 Hz、Base64 分块；每块建议 40-100 ms。输入音频首选 PCM16、单声道、16000 Hz。

## 八、建议目录

```text
astrbot_plugin_quest_avatar_bridge/
  main.py
  metadata.yaml
  _conf_schema.json
  requirements.txt
  core/models.py
  core/session_manager.py
  core/turn_orchestrator.py
  core/interaction_policy.py
  core/intent_parser.py
  transport/http_sse.py
  adapters/astrbot_llm.py
  adapters/stt.py
  adapters/tts.py
  tests/
```

配置至少包含：启用开关、协议版本、认证方式、允许来源、会话上限、队列上限、超时、`chat_provider_id`、STT/TTS 适配器、音频格式、调试日志级别。密钥字段必须标记为敏感，日志不打印密钥、音频、完整对话或 LLM 原始结构输出。

## 九、测试和验收

- 单元测试不依赖真实 AstrBot、LLM、STT、TTS 或网络，全部使用 fake/stub。
- 覆盖：schema、白名单、交互去抖、接受/拒绝两条决策、会话隔离、旧 turn 丢弃、interrupt 后零迟到事件、SSE 慢客户端、异常降级、terminate 清理。
- 集成测试启动临时 HTTP 服务，验证 `session/start -> events -> turn/start -> text delta -> avatar.intent -> reply.end`。
- 增加与 Unity Mock 相同的协议夹具，保证字段和顺序兼容。
- 运行 `python -m pytest -q`、ruff、语法检查；静态用例数量不能表述成“测试通过”。

## 十、交付要求

先给出调查结果和实施计划，再实现代码、测试、README、`CHANGELOG.md` 的 `Unreleased`、配置表和测试命令。不要擅自升级版本、发布、推送或修改其他插件。最终报告实际运行的测试、失败项、当前限制，以及 Unity 对接所需的完整 URL、认证头和示例事件。

## 官方依据

- 新版插件指南：https://docs.astrbot.app/dev/star/plugin-new.html
- 最小插件：https://docs.astrbot.app/dev/star/guides/simple.html
- 消息事件与钩子：https://docs.astrbot.app/dev/star/guides/listen-message-event.html
- AI / `llm_generate`：https://docs.astrbot.app/dev/star/guides/ai.html
- 插件配置：https://docs.astrbot.app/dev/star/guides/plugin-config.html
- 插件 Pages、Web API 与 SSE：https://docs.astrbot.app/dev/star/guides/plugin-pages.html
- 存储：https://docs.astrbot.app/dev/star/guides/storage.html

