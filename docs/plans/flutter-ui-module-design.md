# Banxia Flutter UI 模块设计（壳层重构方案）

> 范围：把 `BanxiaUiShell`（UI Toolkit 双端壳）、`CompanionWorldMenu`（Quest 旧 IMGUI
> 指针菜单）、`BanxiaQuestWorldUiHost`（世界空间 RT 宿主）的**全部工作流与 M1–M3 语义**
> 平移到一个 Flutter 壳层。3D 场景/物理/对话/动画仍留在引擎核心，Flutter 只做 2D 覆盖层
> 与命令入口，二者经一条 JSON 桥通信。
>
> 原则（继承 CLAUDE.md）：业务逻辑平台无关、`#if BANXIA_PHONE` 只隔离平台壳层；
> 本设计即「平台壳层」的 Flutter 化，不改变任何核心语义。

---

## 1. 架构总览

```
┌────────────────────────── Flutter (2D overlay) ──────────────────────────┐
│  RootShell ── 底部 Tab(伴夏/对话/动作/设置) ── SceneOverlay(场景态)         │
│        │ state 只读投影（ChangeNotifier/Riverpod）                          │
│  BridgeClient ── JSON-RPC ──▶ MethodChannel「banxia.bridge」                │
└───────────────┬───────────────────────────────────────────────────────────┘
                │ 命令(→) / 事件+回复(←)   EventChannel「banxia.events」
┌───────────────▼───────────────────────────────────────────────────────────┐
│  引擎核心（Unity 渲染 + 平台无关层）                                         │
│  QuestMmdPlayerBootstrap · RuntimeMmdModelLoader · VmdActionLibrary ·      │
│  ConversationController · VoiceInput · Pairing/AstrBot · QualitySettings · │
│  PhoneCoPresenceDirector · CallFramingSolver · PhoneOrbitCamera ·          │
│  RuntimePerformanceMonitor · RuntimeDiagnosticsBuilder · UpdateChecker      │
└────────────────────────────────────────────────────────────────────────────┘
```

- **单向数据流**：UI 动作 → `BridgeClient.call(cmd, payload)` → 引擎执行 → 引擎
  `push(event, payload)` 回推状态 → `AppState` 更新 → 视图重建。UI 不直接持有引擎状态，
  一切以事件回推为真源（对应现有 C# 里 `StatusChanged/StateChanged/PlaybackChanged/
  QualityChanged` 事件族）。
- **请求/响应**：命令带 `id`，引擎回 `reply{id, ok, data|error}`（异步动作如
  `model.load` / `action.refresh` / `update.check`）。
- **桥只传纯 JSON**，不传 widget 树；布局派生（M1/M3）在 Flutter 侧由
  `MediaQuery`/`LayoutBuilder` 计算，再把关键实测值（chrome insets）回写引擎。

---

## 2. 屏幕清单（Screens）

### 2.1 `RootShell`（根壳，对应 `BanxiaUiShell.ApplyMode` 的 Menu/Scene 二态）
- 状态：`uiMode ∈ {Menu, Scene}`、`currentTab ∈ {Companion, Chat, Actions, Settings}`、
  全局 `toast`。
- 布局：`Scaffold` + `BottomNavigationBar`（4 项）+ 全局 `Overlay`（toast 层，z 最高）。
- `Scene` 态切换为全屏 `SceneOverlay`（对应 `mainUi` 隐藏 / `sceneToolbar` 显示）。

### 2.2 `CompanionScreen`（伴夏/首页）
- 模型列表（`ModelCard`：显示名/大小/“使用中”徽标/进入|查看/删除二次确认）
- 空态提示、导入状态行、`导入模型 / 动作文件`、`刷新模型列表`
- 快捷入口 tiles：对话 / 动作 / 设置 / 更新
- 动作：`model.discover/load/delete/import`、`copresence.enterScene`、切 Tab。

### 2.3 `ChatScreen`（对话）
- 未连接态：连接徽标 + 引导卡「去设置绑定」（跳 Settings/Connection）
- 已连接态：状态卡（伴夏+会话状态）· `ChatBubbleList`（24 条上限，用户/回复两侧）
  · `VoiceControls` · `QuickPhrases`（6 句）· `ChatInputBar`（输入+「拍」+「发」）
- 动作：`conversation.send`、`conversation.sendWithCamera`（权限开关 gating）、
  `conversation.interrupt`、`voice.toggleListen/toggleRecord/restart/cancel`。

### 2.4 `ActionsScreen`（动作）
- 待机预设行 · 切换待机 / 停止动作 / 暂停继续
- 表情轮换 / 刷新动作 / 导入 VMD
- `ActionCard` 列表（名称/时长/帧数/含表情/播放中徽标/播放|删除）
- 动作：`idle.cycle`、`action.refresh/play/stop/delete`、`expression.cycle`、
  `avatar.command{toggle_pause}`、`model.import`。

### 2.5 `SettingsScreen`（设置，根列表 + 二级页，对应 `settingsRootList`/详情页）
根列表（分组入口行）→ 二级详情页（`< 设置` 返回 + 标题 + 滚动内容）：
- **Connection**：服务器域名/IP:端口、私网 HTTP 开关、6 位配对码点 + `PairingNumpad`
  （M3）、实时连接状态、连接后端 / 重新连接 / 解除绑定。
- **Quality**：渲染画质分段（性能/平衡/清晰）、MMD 物理分段（性能/平衡/精细）、
  恢复默认画质、画质状态行。
- **General**：场景诊断 HUD / 构图网格 / 摄像头单帧 三个开关、摄像头单帧说明、
  目标帧率分段（30/60/120）、音量滑杆。
- **Performance**：性能采样摘要（`diagnostics.performance` 快照）。
- **About**：版本 / 设备 / 内存。
- **Update**：检查更新 + 进度条 + 状态行。
- **Log**：刷新日志 / 清空日志（最近 12 行）。

### 2.6 `SceneOverlay`（场景态，对应场景工具栏 + 同框三模式 chrome）
- `SceneToolbar`：主界面(返回) / 移动(切换单指移动) / 模式|环境 / 取景 / HUD。
- `VideoCallChrome`：顶栏（伴夏+计时+字幕）+ 底部控件（挂断/模式/去聊天）。
- `CopresenceSheet`（M2）：mode cards（同框现实/虚拟场景/视频通话）与
  environment chips（夜街/星空/卧室/海边）+ grabber + `换种同框方式`。
- `ArPlaceHint`：AR 放置提示（不拦截点击）。
- `FramingGrid`（M1）：红安全区 + 绿手机 42% 眼线 + 锚点十字 + 左上角 d/h/eye% 数值。
- 动作：`copresence.switchMode/switchEnvironment/enterScene/returnToMenu`、
  `copresence.arPlace`、`scene.moveMode/reframe/hud`、`copresence.setChromeInsets`。

### 2.7（Quest 独占扩展，`platform` 门控）`WorldMenuExtension`
对应 `CompanionWorldMenu` 中手机端没有的硬件独占入口：重新放置 / 站立校准 /
彩色透视 / 扫描房间 / 面对面放置 / 描边三键 / 设备性能实时面板 / 运行诊断侧栏。
在 Flutter 中作为 `ShellExtension` 挂件按平台注入，双端共用能力（动作/外观/模型/
画质/语音/文字/配对/诊断）仍走统一 Tab 与 Settings，不重复实现。

---

## 3. 状态模型（AppState 聚合 + 分域）

| 域 | 关键字段 | 事件源 |
|----|---------|--------|
| `ConnectionState` | bridgeStatus, pairingStatus, pairingCode, server, privateHttp, connected | `connection.changed` `pairing.*` |
| `ConversationState` | state, transportStatus, transcript, replyText, lastError | `conversation.state/transcript/reply` |
| `ModelLibraryState` | models[], currentPath, importStatus, loading | `model.updated/importStatus` |
| `ActionLibraryState` | actions[], playingId, idlePreset, expression, refreshing | `action.updated/playbackChanged` |
| `QualityState` | renderPreset, physicsPreset, fps, volume, status | `quality.changed` |
| `CoPresenceState` | mode, environment, videoCallActive, callDuration, chromeTop/chromeBottom, arPlaced, arAvailable, sheetKind, sheetOpen | `copresence.*` |
| `SettingsState` | hud, framingGrid, camera | 本地 + `settings.toggled` |
| `UpdateState` | checking, progress, version, hasUpdate | `update.*` |
| `DiagnosticsState` | logLines[], perfSummary, fps, poseSrcFlip | `log.updated` `performance.snapshot` |
| `Toaster` | message, hideAt | `toast` |

**派生规则（保留 C# 语义）**：
- 聊天页未连接/已连接 UI 分支 = `ConnectionState.connected`。
- Action 卡「播放中」徽标 = `playingId == action.id`。
- Settings 分段选中态 = `QualityState.renderPreset/physicsPreset/fps` 与选项比对。
- 场景 chrome 显隐 = `uiMode==Scene` 且 `CoPresenceState.mode==VideoCall`。

---

## 4. M1–M3 语义映射（不变量逐一保留）

### M1 · 通话构图闭式解（INV-1/2/7）
- `CallFramingSolver` **留在引擎**（平台无关，不做 Flutter 化）。
- Flutter 侧职责：`VideoCallChrome` 布局完成后量测顶栏底缘 `T`、底控件顶缘 `B`
  （物理像素），经 `copresence.setChromeInsets{top,bottom}` 回写；分辨率变化时重发
  （对应 `PushChromeInsets` + `GeometryChangedEvent`）。
- QA 叠加层 `FramingGrid` 消费引擎 `framing.anchors{anchorKind, eyeLinePct, d, h}` 事件
  绘制十字标/安全带/数值（INV-7 可观测）。求解日志行 `[M1] d=… h=… eye%=…` 保持
  引擎 logcat 输出不变，供模拟器断言。

### M2 · 弹层状态机（INV-5）
- Flutter 天然 `Stack`：`SceneToolbar/VideoCallChrome`(底) < `ModalBarrier`(scrim) <
  `CopresenceSheet` < `Toast`。
- 打开 sheet：隐藏 `.call-controls`（不是半透明，是**不可见不可点**）、显示 scrim
  `rgba(0,0,0,0.4)`、sheet 置顶；关闭三条路径（点遮罩 / grabber 下拉≥120px / 选卡自动）
  统一还原控件并清 scrim。`uiMode`/模式切换兜底清残留（对应 `UpdateCoPresenceChrome`）。
- 动画：scrim opacity 120ms、sheet 平移 160ms ease-out（`AnimatedOpacity`/`SlideTransition`）。

### M3 · 配对键盘派生重排（INV-3/4）
- `PairingNumpad` 用 `LayoutBuilder` 派生：`H = screenH/16`；`W` 由卡内宽派生；
  `F = 0.42·H`；`R = H/2`（精确胶囊）；3×4 布局 `1..9 / ⌫ 0 ✓`。
- ⌫ 短按退格、长按(≥600ms)清空；✓ 提交（码不足 6 位走既有 toast）。数字
  `Center` 对齐（INV-4 <8px）。圆角令牌收敛（`--radius-card` 等），零 9xxpx 硬值。

---

## 5. Dart 文件分解

```
lib/
  main.dart
  app/
    banxia_app.dart                 # MaterialApp + 路由 + theme 注入
    theme/banxia_theme.dart         # iOS 深色令牌（bg F2F2F7 / tint / glass / radius）
    theme/design_tokens.dart        # 圆角/间距/字号令牌（M3 防复发登记）
  core/bridge/
    bridge_channel.dart             # MethodChannel「banxia.bridge」+ EventChannel「banxia.events」
    bridge_protocol.dart            # cmd/event 名称常量 + 信封类型
    bridge_client.dart              # call(cmd,payload) → Future<Reply>；on(event, handler)
    message_models.dart             # ModelInfo/VmdActionInfo/PerfSnapshot 等 JSON 模型
  state/
    app_state.dart                  # 聚合 + 事件分发入口
    connection_state.dart
    conversation_state.dart
    model_library_state.dart
    action_library_state.dart
    quality_state.dart
    copresence_state.dart
    diagnostics_state.dart
    update_state.dart
    toaster.dart
  screens/
    shell/root_shell.dart
    companion/companion_screen.dart
    companion/widgets/model_card.dart
    chat/chat_screen.dart
    chat/widgets/chat_bubble_list.dart
    chat/widgets/chat_input_bar.dart
    chat/widgets/voice_controls.dart
    chat/widgets/quick_phrases.dart
    actions/actions_screen.dart
    actions/widgets/action_card.dart
    settings/settings_screen.dart
    settings/pages/{connection,quality,general,performance,about,update,log}_page.dart
    settings/widgets/pairing_numpad.dart      # M3
    settings/widgets/{segmented_row,toggle_row,slider_row,info_row}.dart
  scene/
    scene_overlay.dart
    widgets/scene_toolbar.dart
    widgets/video_call_chrome.dart
    widgets/copresence_sheet.dart             # M2
    widgets/ar_place_hint.dart
    framing/framing_grid.dart                 # M1
  qa/
    qa_command_router.dart                    # Android-intent 等价的 QA 命令分发
  platform/
    world_menu_extension.dart                 # Quest 独占入口（platform 门控注入）
```

---

## 6. 桥接消息 Schema（JSON-RPC）

信封（双向一致）：
```jsonc
{ "v": 1, "id": 12, "type": "cmd|reply|event",
  "name": "<cmdOrEvent>", "payload": { }, "error": null }
```

方向约定：`cmd` Flutter→引擎；`event` 引擎→Flutter 主动推送；`reply` 引擎对某 `id`
的应答（`ok` + `data` 或 `error`）。字符串枚举见下。

### 6.1 命令（Flutter → 引擎）

| name | payload | 说明 |
|------|---------|------|
| `model.discover` | `{force}` | 返回 `ModelInfo[]` |
| `model.load` | `{path, packageRoot?}` | 进入场景/切换模型 |
| `model.delete` | `{path}` | 二次确认已在 UI |
| `model.import` | `{}` | 打开系统文件选择器 |
| `action.refresh` | `{}` | 返回 `VmdActionInfo[]` |
| `action.play` | `{id}` | 播放/停止切换 |
| `action.stop` | `{}` | 回待机 + `PlayAction("idle")` |
| `action.delete` | `{id}` | |
| `idle.cycle` | `{}` | |
| `expression.cycle` | `{}` | |
| `avatar.command` | `{name}` | `toggle_pause` / `reset` 等 |
| `conversation.send` | `{text, attachment?}` | 文本/摄像头帧 turn |
| `conversation.interrupt` | `{}` | |
| `voice.toggleListen` | `{}` | |
| `voice.toggleRecord` | `{}` | |
| `voice.restart` | `{}` | |
| `voice.cancel` | `{}` | 录音中取消，否则 interrupt |
| `pairing.setServer` | `{server}` | |
| `pairing.setPrivateHttp` | `{enabled}` | |
| `pairing.digit` | `{op:"append"\|"remove"\|"clear", digit?}` | numpad 语义 |
| `pairing.pair` | `{}` | 校验 6 位码后发起 |
| `pairing.reconnect` | `{}` | ReloadConfiguration |
| `pairing.clearBinding` | `{}` | 删配置 + Reload |
| `quality.applyPreset` | `{preset:"performance"\|"balanced"\|"clear"}` | |
| `quality.applyPhysics` | `{preset:"performance"\|"balanced"\|"fine"}` | |
| `quality.reset` | `{}` | |
| `settings.targetFps` | `{fps:30\|60\|120}` | |
| `settings.volume` | `{v:0..1}` | |
| `settings.toggle` | `{key:"hud"\|"framingGrid"\|"camera", value}` | |
| `copresence.enterScene` | `{path?}` | null=恢复上次模型 |
| `copresence.returnToMenu` | `{}` | Suspend + 回 Menu |
| `copresence.switchMode` | `{mode:"arReality"\|"virtualScene"\|"videoCall"}` | |
| `copresence.switchEnvironment` | `{env:"nightStreet"\|"starrySky"\|"bedroom"\|"seaside"}` | |
| `copresence.setChromeInsets` | `{top,bottom}` | M1 实测注入 |
| `copresence.arPlace` | `{x,y}` | 屏幕坐标点地放置 |
| `scene.moveMode` | `{}` | 切单指移动 |
| `scene.reframe` | `{}` | |
| `scene.hud` | `{}` | |
| `update.check` | `{}` | |
| `update.install` | `{}` | |
| `log.refresh` | `{}` | 返回最近 12 行 |
| `log.clear` | `{}` | |
| `qa.command` | `{name, args:{}}` | 映射现有 intent QA 命令 |

### 6.2 事件（引擎 → Flutter）

| name | payload | 说明 |
|------|---------|------|
| `connection.changed` | `{connected, bridgeStatus}` | |
| `pairing.status` | `{status, privateHttp, codeLen}` | |
| `conversation.state` | `{state, transportStatus, lastError?}` | |
| `conversation.transcript` | `{text}` | |
| `conversation.reply` | `{text}` | |
| `model.updated` | `{models[], currentPath}` | |
| `model.importStatus` | `{status}` | |
| `action.updated` | `{actions[]}` | |
| `action.playbackChanged` | `{playingId}` | |
| `quality.changed` | `{renderPreset, physicsPreset, status}` | |
| `copresence.mode` | `{mode, environment, videoCallActive, arAvailable}` | |
| `copresence.callTimer` | `{durationText}` | |
| `copresence.chromeInsetsNeeded` | `{}` | 请求 Flutter 重测 T/B |
| `framing.anchors` | `{anchorKind:"head"\|"bounds", eyeLinePct, d, h}` | M1 叠加层数据 |
| `voice.status` | `{monitoring, alwaysListening, recording, level}` | |
| `update.status` | `{phase:"checking"\|"downloading"\|"installing"\|"idle", progress?}` | |
| `log.updated` | `{lines[]}` | |
| `performance.snapshot` | `{fps5s, fps30s, frameP50Ms, frameP95Ms, physicsDropS, poseSrcFlip}` | |
| `toast` | `{message}` | 引擎侧主动提示（如导入结果） |

### 6.3 兼容保证
- QA 命令（`toggle_menu` / `open_model_list` / `load_first_model` / `capture_first_model` /
  `open_import` / `SimulateContactForQa` / `open_world_ui` / `open_text_input` / `send_text` /
  `run_vmd_qa` / `run_performance_qa`）统一收进 `qa.command`，参数按原 intent extra 透传；
  引擎侧保留原 `[BanxiaQA]` 日志格式与 `performance_result` 字段名，模拟器/断言脚本零改动。
- 现有 `banxia.phone.*` / `Banxia.Debug.AutoScroll` 等 PlayerPrefs 键名不在桥上暴露，
  仍由引擎维护；Flutter 通过 `settings.toggle` 语义命令触发。

---

## 7. 验收要点（映射到既有断言）

- INV-1/2/7：`framing.anchors` 事件 + `FramingGrid` 渲染 + 引擎求解日志行（M1）。
- INV-5：sheet 打开帧 call-controls 不可见不可点 + scrim 压暗 + 三路径关闭（M2）。
- INV-3/4：numpad 派生 `H=s/16, R=H/2` + 数字居中（M3）。
- 双端同步：共享 Tab/Settings/Scene 壳两端一致；Quest 独占入口走
  `WorldMenuExtension` 平台注入，业务能力不重复实现。
