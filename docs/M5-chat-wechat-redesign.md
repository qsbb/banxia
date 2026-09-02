# M5 · 对话页微信化 + 设置连接页修复（盲测驱动）

> 状态：方案已定稿（用户三项决策：全程模拟器盲测 / 建议回复走后端 LLM / 语音收进输入条切换）。
> 前置版本：v0.3.2（HEAD `eec5608`）。目标：修复用户报告的 6 个 UI 问题 + 建立建议回复协议。

## 0. 用户报告原文 → 问题编号

| # | 用户描述 | 归位 | 状态 |
|---|---------|------|------|
| P1 | “中间还多了个语音可选，那个常显了显得过于冗余” | 对话页 `AddVoiceControls` | 已复现 |
| P2 | “输入的文字过长时，会把右边的按钮顶到画面外面去” | `.chat-input-bar .field` | 已复现（代码级） |
| P3 | “上面的预设词建议去掉，换成根据他的回复自动生成的 3 条我的快速回复，从上到下一二三条” | `AddQuickPhrases` 静态 chips | 已复现 |
| P4 | “对话改成类似微信、QQ 那样的一个对话窗口” | `BuildChatPage` 整体结构 | 已复现 |
| P5 | “『连接后端』输入 IP 的文本框过长，直接突出到屏幕外面” | `FillConnectionSection` + `MakeElementRow` | 已复现 |
| P6 | “上下滑动的界面太长，空白多，要滑动才能看到『解除绑定』” | 同上（一页塞三类任务） | 已复现 |

## 1. 盲测复现记录（v0.3.2，模拟器 1080×2340）

复现纪律：不看代码、只按用户操作路径走，截图后做像素几何分析。

**证据 R1（对话页，未连接态）** `repro-01-chat.png`
- 引导卡 y600–898，下方 y914–2096 共 1182px 完全空白（背景色 242,242,247，零非背景像素）。
- 输入条与 chips 被 `RefreshConnectionUi`（L1430-1436）在未连接时 `display:None`。
- 结论：未连接态无输入条是既有设计；P1/P2 的复现须在连接态，见 R1'。

**证据 R1'（用户真机截图 1440×3200，已连接态）**
- y2160–2880 整块 720px 白卡 = “语音（可选）”分组（`AddVoiceControls` 全家：常开监听开关 + 6 小按钮 + 多行麦克风状态），占屏 22.5%。
- 状态卡 y320 / 消息流之后语音卡之前，即消息流被语音块和预设 chips 夹在中间 —— P1/P4 直接证据。

**证据 R2（P5，模拟器连接页）** `repro-04-connection.png`
- 输入区灰底 bbox (159,700)–(1074,877)；卡片右缘 x1020 → **输入区越出卡片 54px，距屏幕物理右缘仅 6px**。
- 字段内部 label“服务器域名 / IP:端口”占 x144–717 共 573px；行内还有 row-label“服务器”（x144–273）→ 双标签 + 无收缩约束。

**证据 R3（P6，模拟器连接页）** `repro-04-connection.png`
- 白卡 (57,537)–(1020,2280) 直伸到 tab 栏（实测 y2100–2277）底下；主按钮“连接后端”蓝色像素到 y2205（被 tab 栏遮挡），“解除绑定”更靠下 → 首屏不可见，必须滑动。
- 高度构成：返回行 220 + navbar ≈120 + 服务器行 120 + HTTP 开关行 120 + 配对码 label/dots ≈120 + **numpad 4 行 × 屏高/16 ≈ 585** + 状态行 + 3 个主按钮 ≈360 → 合计 ≈1700px，可用 ≈1540px。

**证据 R4（P3）** `repro-01-chat.png` 未连接态 chips 未渲染，但代码 L1160 静态数组 `{"你好","你是谁","现在几点","还记得我吗","跳个舞","链路测试"}` + 真机已连接截图 —— 静态预设词常显于输入条上方，与“根据回复生成”的需求不符。

## 2. 第一性原理分析

### P2 输入条顶出按钮 —— flex 布局的因果链
1. `.chat-input-bar` = flex row（nowrap）：`field(flex-grow:1)` + `拍(120px)` + `发(120px)`。
2. UI Toolkit/Yoga 的 flex 项有**内容最小宽度**（min-width:auto）：TextField 内部文本不换行 → 文本多长，min-width 多长。
3. `flex-shrink` 只能把项收缩到 min-width 为止（本文件 USS 未设 shrink/min-width/overflow，field 的 flexGrow=1 不能对抗 min-width 增长）。
4. 于是行总宽 = max(可用宽, 文本宽+240px+padding)。文本超长 → 行溢出 → 拍/发被顶出屏幕右缘。
5. **修复本质**：打断第 2 步——给 field 显式 `min-width:0` + `flex-shrink:1` + `overflow:hidden`，让“可收缩下限”脱离“文本内容宽度”，文本在字段内部截断/滚动而非撑破行。

### P5 服务器字段溢出 —— 同一根因的设置页变体
1. `MakeElementRow("服务器", field)` 只设 `element.flexGrow=1`；row-label 129px。
2. TextField 自带 label（573px）+ 输入区 → 内容宽 ≈915px；行可用 ≈966px；129+915=1044 > 966 → 溢出 78px。
3. 双重标签本身违反信息架构：行已有“服务器”，字段内 label 冗余。
4. **修复**：① 去掉字段内 label（改字段下方 status-line 提示“域名或 IP:端口”）；② `MakeElementRow` 给 element 统一加 `flexShrink=1; minWidth=0; overflow=hidden`；③ `.field` USS 同步兜底。

### P1/P4 语音块冗余 + 对话页结构 —— 信息架构错位
1. 对话窗口的本质职责 = **消息流 + 输入入口**（微信/QQ 范式）。
2. 当前页面在两者之间插入了两块非对话内容：语音配置面板（P1）+ 静态预设词（P3），把消息流压缩成“三明治夹心”。
3. 语音配置面板里混了三种粒度的东西：**配置项**（常开监听）、**输入动作**（录音/取消）、**会话控制**（打断/暂停动作）。微信的解法是把语音变成输入入口的一个**模式**（键盘↔语音切换），配置收进设置，会话控制随动作页。
4. **修复**：输入条左侧加切换钮 → 语音态变为“按住 说话”条（PointerDown=StartRecording，PointerUp=StopAndSend，上滑 120px=CancelRecording），“常开监听”迁设置→通用，麦克风状态并入状态卡副标题。

### P3 预设词 → 智能建议 —— 数据源的替换
1. 静态 chips 的问题是数据源与对话无关；建议回复的本质 = **以伴夏最新回复为条件的条件生成**，属后端职责（用户已决策 LLM 生成）。
2. 协议约束：`reply.end` 是回合收尾标记，不能被第二个 LLM 调用阻塞 → 建议走**独立事件 `reply.suggestions`，在 reply.end 之后异步发出**（迟到、失败、缺省都只影响 chips，不阻塞回合）。
3. UI：3 条纵向卡片（一二三条）位于消息流与输入条之间；空/未生成时整块隐藏（不占空间）；点击 = 直接发送（快速回复语义）。

### P6 连接页过长 —— 一页三类任务
1. 该页混了三类低频不同步的任务：地址配置（常驻）、配对码录入（仅首次/换绑）、维护操作（重连/解绑）。
2. numpad 585px 是为“配对码录入”服务的，但配对码录入是**低频事件**——常驻展示是浪费 38% 屏高。
3. **修复**：折叠。默认收起 numpad（显示“配对码”折叠行，点击展开）；无绑定记录时自动展开（onboarding 场景正好需要）；重连/解绑压缩为一行两个半宽按钮。

## 3. 实施方案

### M5A 对话页微信化（BanxiaUiShell.cs + BanxiaTheme.uss）
- 删除 `AddVoiceControls` 调用与实现（对话页不再出现“语音（可选）”分组）。
- `BuildChatPage` 连接态结构改为：状态卡 → `chatTranscript`（flexGrow=1，占满） → 建议列表（可隐藏）→ 输入条。
- 输入条重排：`[语音/键盘切换钮] [field 或 按住说话条] [拍] [发]`；语音态隐藏 field 与发钮，显示 hold 条。
- 语音态 hold 条：PointerDown→`StartRecording`、PointerUp→`StopAndSend`、上滑≥120px 取消（`CancelRecording`）+ 提示“松开手指，取消发送”。
- “常开监听”开关迁 `FillGeneralSection`；`voiceStatusLabel` 并入状态卡副标题（`chatStateLabel` 追加一行麦克风摘要）。
- “打断回复”保留 hold 条上滑取消语义；“暂停动作”已在动作页存在（L1620“暂停/继续”），删除重复。

### M5B 建议回复协议（双端）
**Unity 侧**
- `AstrBotProtocol.cs`：新增 case `"reply.suggestions"` → `ConversationEventType.ReplySuggestions`，解析 `suggestions`（≤3 条、各 ≤200 字符、strip）。
- `ConversationStateMachine.cs`：存 `SuggestedReplies`（数组），收到新建议覆盖旧值；用户发送后清空。
- `BanxiaUiShell.cs`：`AddQuickPhrases` 替换为 `BuildChatSuggestions`（3 条纵向、序号+文本、点击直发）；空时隐藏。

**插件侧（astrbot_plugin_embodiment_bridge）**
- 新增 `core/reply_suggestions.py`：`ReplySuggestionService`——跟随 `fast_action` 的适配器模式（独立 provider、独立超时 6s、可用性检查），prompt = 最近 6 轮对话滚动窗口 + “以用户口吻生成 3 条简短回复，JSON 数组输出”。
- `turn_orchestrator.py`：`reply_end` 发出后 `asyncio.create_task` 生成建议（超时/失败静默放弃，诊断计数 `suggestions_emitted/failed/timeout`），成功则 `_emit` 新事件 `reply.suggestions`。
- `_conf_schema.json`：`reply_suggestions_enabled`（默认 true）、`reply_suggestions_provider_id`（缺省回落主 provider）。
- pytest：新增协议解析/超时/关闭路径单测。

### M5C 设置连接页（折叠 + 溢出）
- `MakeElementRow`：element 统一 `flexShrink=1; minWidth=0; overflow=Hidden`。
- `FillConnectionSection` 重排：
  - 服务器字段去内 label，label 文案收进行提示（“服务器”行下方 status-line：“域名或 IP:端口，如 192.168.5.55:25520”）。
  - numpad 折叠：默认收起（“配对码 ▸”行）；首次无绑定自动展开；`pairing_code` 6 位齐 → 自动收起。
  - “重新连接 / 解除绑定”压成一行两个半宽按钮（`MakeButtonRow`）。
- USS：`.field { min-width: 0; }`、`.chat-input-bar .field { overflow: hidden; }`、新增 `.chat-hold-bar`、`.chat-voice-toggle`、`.chat-suggestion-*`、`.row-collapse` 样式。
- 二级页滚动底部 padding ≥ tab 栏高度（228px），消除内容被 tab 栏遮蔽。

### 双端同步登记（PHONE_PORT_PLAN_CN.md）
- 建议回复协议（`reply.suggestions` 事件 + 3 条纵向 UI）为平台无关功能 → Quest VR 端待同步（盲测完成登记）。
- 输入条 flex 收缩修复、连接页折叠：USS/Flutter 侧同构改动登记待同步。

## 4. 盲测计划（复测 = 同路径新包）

1. **构建**：tar → 5.55 `banxia_build_phone_wait.ps1` → APK 校验（size/SHA/ZIP）。
2. **安装**：5.21 `adb install -r -d`。
3. **对话页盲测**（5.21 起 mock SSE 后端，见 §5）：
   - 走 Chat tab：截图断言 ①无“语音（可选）”白卡 ②消息流 flexGrow 占满 ③输入条四元素完整在屏内（右缘 x≤1026）。
   - adb 长文本注入失败风险 → 用 TouchScreenKeyboard native IME 或几何断言（field 右边界恒 = 发钮左缘-16px，不随文本变化即证明收缩生效）。
   - mock 后端发 `reply.suggestions` → 断言 3 条纵向卡片出现（1/2/3 顺序）；点第 2 条 → 断言用户气泡=该文本且 chips 清空。
   - 语音切换钮 → 断言 hold 条出现、field 隐藏；按住（input swipe 模拟 press-hold-release）→ logcat 断言 `StartRecording`→`StopAndSend`。
4. **设置页盲测**：
   - 连接页截图断言：①字段右缘 ≤1020（卡内）②首屏可见“解除绑定”按钮（无滑动）③numpad 收起（页面高度骤减，白卡高度 ≤1400）。
   - 点“配对码”行 → numpad 展开；输码（swipe 数字）→ 状态行变化。
5. **回归**：QA assert 脚本全跑（framing/overlay/layer/shape/align）。
6. 通过 → commit+push（M5 独立提交）→ release 更新 → NAS 发布 → 模拟器 force-stop + qemu 释放。

### §5 mock 后端（5.21，python）
最小 SSE 服务器实现 `session/start`→`turn/start`→`events/<id>`，脚本化输出：
`asr.final → reply.text.delta → reply.end → reply.suggestions(3条)`。
用途仅盲测建议链路与消息流渲染；真实 LLM 链路由用户真机验收（用户已选模拟器盲测，真机为后续）。

## 5. 风险与回滚
- 语音 hold 手势在模拟器上 `input swipe` 按压时长可控（`input swipe x y x y 1500`）→ 可盲测。
- `reply.suggestions` 为新增事件：旧客户端未知事件已有 default 分支报错（`AstrBotProtocol` L420-422 "Unsupported SSE event type"）→ **必须先发 Unity 新包**，插件后发；插件开关默认开但生成失败静默，双端任意单端升级都安全。
- 折叠默认收起可能影响已绑定用户的重配对习惯 → “配对码”折叠行放在“服务器”组内同卡，一步可见；解绑/重连仍一键可达。
- 回滚：单 commit revert 即可（USS/C# 无数据迁移；插件建议服务开关可关）。

## 6. 交付物清单
- `docs/M5-chat-wechat-redesign.md`（本文档）
- `Assets/Scripts/UI/BanxiaUiShell.cs`（对话页重构 + 建议列表 + hold 条）
- `Assets/Scripts/Backend/AstrBotProtocol.cs` + `ConversationStateMachine.cs` + `ConversationController.cs`（建议事件）
- `Assets/UI/Resources/BanxiaTheme.uss`（新样式 + 溢出修复）
- `astrbot_plugin_embodiment_bridge/core/reply_suggestions.py` + `turn_orchestrator.py` + `_conf_schema.json` + tests
- `PHONE_PORT_PLAN_CN.md`（待同步登记）
- 5.21 `banxia-mock-bridge.py`（盲测 mock 后端，测试资产）
