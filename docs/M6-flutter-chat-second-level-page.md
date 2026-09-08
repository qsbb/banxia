# M6 · Flutter 对话页迁出底部标签 → 微信式二级页（设计稿）

> 状态：设计稿（UI 设计子代理产出，未改代码）。
> 现状基线：`flutter_ui/lib/`（RootShell 四标签 IndexedStack；`chat_screen.dart` 输入框被悬浮导航胶囊遮挡）。
> 关联文档：`docs/M5-chat-wechat-redesign.md`（语音收进输入条、建议回复协议已定稿；本文档在其上解决**页面层级**与**遮挡根治**）。
> 约束：引擎侧 Cmd/Evt 协议零变更（§4）；所有样式引用 `main.dart` L13-47 `BanxiaTokens`。

---

## 1. 遮挡根因（先说清楚为什么现在会被挡）

`_MenuShell`（`root_shell.dart` L74-103）的结构是：

```
Stack
 ├─ IndexedStack(…, ChatScreen, …)      // 内容层，占满全屏
 └─ Align(bottomCenter) → _BottomNav    // 悬浮玻璃胶囊，浮在内容之上
     height:64 + margin(bottom:20) + viewPadding.bottom
```

- `_BottomNav` 不是 Scaffold 的 `bottomNavigationBar`，而是**浮层**。`ChatScreen` 自身 `SafeArea(top:true, bottom:false)`（`chat_screen.dart` L21-23），底部只留 `EdgeInsets.fromLTRB(20, 8, 20, 12)`（L382）——**没有任何地方为悬浮导航让出 64+20+手势区的高度**，输入条必然被胶囊盖住。
- 键盘弹出时靠 `RootShell` Scaffold 的 `resizeToAvoidBottomInset:true`（L30）+ L90 `viewInsets.bottom>0 时隐藏导航` 侥幸避让；键盘收起即复发。
- **结论**：遮挡不是 padding 数值问题，是"内容页不知道浮层存在"的结构问题。把对话页迁出标签体系、成为独立路由后，该路由内**根本不存在底部导航**，问题结构性消除（§3）。

---

## 2. 信息架构

### 2.1 底部导航：4 → 3 标签

迁走「对话」后底部导航剩 **3 个标签**：

| 顺序 | 标签 | 图标（沿用 `_BottomNav._tabs` 现有项） | 说明 |
|---|---|---|---|
| 1 | 首页 | `Icons.person_outline` | 不变，新增对话入口卡（§2.2） |
| 2 | 动作 | `Icons.auto_awesome_outlined` | 不变 |
| 3 | 设置 | `Icons.settings_outlined` | 不变 |

- `AppTab` 枚举（`app_state.dart` L16）收缩为 `{companion, actions, settings}`；`IndexedStack`（`root_shell.dart` L81-88）同步删 `ChatScreen` 槽位。`AppTab.chat` 的所有引用（`companion_screen.dart` L251 快捷瓦片、`chat_screen.dart` L134 引导卡跳设置反向保留）一并迁移。
- `_BottomNav` 胶囊样式（高 64、`margin 20/0/20/20`、圆角 32、`Color(0xEBFFFFFF)` 玻璃底）**不变**，只是 3 等分更宽，更接近 iOS 观感。
- `handleSystemBack`（`app_state.dart` L476-498）的 `tab.value != AppTab.companion → 回首页` 语义不变；其 `nav.canPop() → pop()` 分支（L486-489）已天然支持二级页，**无需改动**。

### 2.2 首页入口：「对话」英雄卡

位置：`CompanionScreen` 的 sliver 列表中，`_NavBar` 之后、`_ImportRow` 之前——**首屏第一张卡**。不再用「快捷入口」里 88 高的小瓦片（现有 `_Tile`，`companion_screen.dart` L282）承载最高频功能；`_QuickTiles` 中的「对话」瓦片删除，其余（动作/设置/更新）保留。

样式（全部复用现有令牌与卡片惯例，对标 `_ModelCard` L113-164）：

```
Container(
  margin: EdgeInsets.fromLTRB(20, 0, 20, 10),
  padding: EdgeInsets.all(16),
  decoration: BoxDecoration(
    color: BanxiaTokens.bgCard,                       // #FFFFFF
    borderRadius: BorderRadius.circular(BanxiaTokens.radiusCard), // 20
  ),
  child: Row(
    [ 36×36 圆头像 (BanxiaTokens.tintFill 底 + Icons.person 白, 同 _StatusCard L159-167) ]
    [ Expanded: '和伴夏聊聊' 17 bold label
               副标题: 连接状态·最近一条回复摘要（labelSecondary 13, 单行 ellipsis） ]
    [ 未读/状态点: connected ? green : labelTertiary 的 8px 圆点（同 _NavBar 徽章 L67-96） ]
    [ Icon(Icons.chevron_right, labelTertiary) ]     // 同 _NavRow 的 chevron 惯例
  ),
)
```

- 副标题数据源：`conversation.bubbles` 最后一条非用户气泡的截断文本（纯 UI 派生，无协议变更）；未连接时显示「未连接 · 点我去绑定」。
- 点击：`Navigator.of(context).push(MaterialPageRoute(builder: (_) => ChatPage(appState: appState)))`——与设置页 `_push`（`settings_screen.dart` L131-140）**同一路由模式**，全 App 二级页手势统一。

### 2.3 功能归类：对话页内 vs 留在设置

| 功能 | 归属 | 理由 |
|---|---|---|
| 消息流（24 条气泡） | **对话页内**（主体） | 对话窗口本质职责 |
| 输入栏（文本/发送/相机单帧） | **对话页内** | 输入入口 |
| 语音操作（监听/录音/重启/取消） | **对话页内**，输入条麦克风 `PopupMenuButton`（现状沿用，M5 已定稿） | 输入动作随输入条 |
| 会话状态（state/transport/麦克风态） | **对话页 AppBar 副标题**（吸收 `_StatusCard`） | 微信"对方正在输入…"范式 |
| 建议回复（≤3 条） | **对话页内**，消息流与输入栏之间（现状沿用） | M5 已定稿 |
| 未连接引导 | **对话页顶部横幅**（替代整页 `_GuideCard`） | 微信"当前网络不可用"横幅范式；点横幅 → pop + `switchTab(AppTab.settings)` |
| 绑定/服务器/配对码 | **留在设置** | 低频配置，不进对话页 |
| 摄像头单帧开关、帧率、HUD 等 | **留在设置** | 配置项 |
| 对话页右上角「⋯」 | **不做**（v1） | 无必须项，保持骨架干净 |

---

## 3. 微信式对话页细节设计

### 3.1 页面骨架（新文件 `screens/chat_page.dart`，与 tab 版 `chat_screen.dart` 并存至迁移完成）

```dart
class ChatPage extends StatelessWidget {
  // 结构与设置二级页 _SettingsPageScaffold（settings_screen.dart L294-312）同源
  Scaffold(
    backgroundColor: BanxiaTokens.bg,          // #F2F2F7，不透明（关键，见 §3.4）
    resizeToAvoidBottomInset: true,            // 键盘避让的唯一正确开关（§3.4）
    appBar: AppBar(
      backgroundColor: BanxiaTokens.bg,
      elevation: 0,
      scrolledUnderElevation: 0,
      leading: BackButton(color: BanxiaTokens.tint),          // 与设置二级页一致
      title: Column( '伴夏' 17 bold / 副标题 12 labelSecondary ),
      centerTitle: true,                       // 微信式居中
    ),
    body: Column(
      children: [
        if (!connected) _ConnectionBanner(),   // 未连接横幅（§3.5）
        Expanded(child: _BubbleList()),        // 消息流，复用现有组件
        if (suggestedReplies.isNotEmpty) _QuickPhrases(),   // 复用
        _ChatInputBar(),                       // 复用 + SafeArea 包裹（§3.4）
      ],
    ),
  )
}
```

- **AppBar 副标题**吸收 `_StatusCard` 三行状态为一行：`'{state} · {transportStatus}'`，麦克风态用前缀图标表达（录音中红色 `Icons.mic` 12px / 监听中 tint 色 `Icons.hearing` / 待命无图标）。状态卡（头像+伴夏+三行文本）整卡删除——头像已有首页入口卡承载，对话页内不再重复。
- 消息流 `_BubbleList`、建议 `_QuickPhrases`、输入条 `_ChatInputBar` **组件原样复用**（从 `chat_screen.dart` 提取为共享 widget 或直接迁入新文件后删除旧文件）。

### 3.2 输入栏设计（微信行为）

沿用现有 `_ChatInputBar`（`chat_screen.dart` L342-499）四元素排布：

```
[ 多行输入框 Expanded ] [ 🎤 语音菜单 42×46 ] [ 📷 相机 42×46 ] [ ↑ 发送 46×46 圆形 tintFill ]
```

- 输入框：`BanxiaTokens.glass` 底、圆角 23、`minLines:1 / maxLines:4`、`minHeight 46 / maxHeight 112`——**全部保留现状**（M5 已验证）。
- **贴键盘顶**：键盘弹出时输入条必须紧贴键盘上缘（微信行为）。实现 = Scaffold `resizeToAvoidBottomInset:true` + 输入条是 `body: Column` 的最后一个孩子。body 高度被 Scaffold 减去 `viewInsets.bottom`，Column 底 = 键盘顶，输入条自然贴上。**不要**给输入条自己加 `viewInsets` padding（双重避让会把输入条顶到键盘上方一条屏高）。
- **键盘收起时的手势条**：输入条外包 `SafeArea(top: false, child: …)`，底部只补系统手势区，不补别的。
- 收起键盘手势：消息流 `keyboardDismissBehavior: onDrag`（现有 L246）保留——下拉消息流收键盘，微信同款。

### 3.3 返回语义

- AppBar `BackButton` → `Navigator.pop` → 回首页（首页状态因 `IndexedStack` 保活，滚动位置/模型列表不丢）。
- **系统返回键**：Android 宿主把 KEYCODE_BACK 推为 `Evt.systemBack` → `AppState.handleSystemBack()`（`app_state.dart` L476）既有优先级：`canPop() → pop()`（L486-489）在"回首页 tab"与"最小化"**之前**，对话页 pop 后落到首页 tab，再按一次走既有 `systemMinimize`。**零改动即可用**（该机制为"画质设置"二级页所建，对话页直接复用）。
- **会话状态保持**：`ConversationState`（bubbles/suggestions/recording/monitoring/state）挂在 App 级 `AppState`（`app_state.dart` L141），不随路由销毁——pop 再进，气泡、建议、录音状态全部还在。**唯一易失物**是 `_ChatInputBar` 内的 `TextEditingController` 草稿（L352）。v1 接受清空（与微信"草稿保留"的差异列入 backlog；若要做，把 draft 字符串上移到 `ConversationState`，纯 UI 侧变更）。

### 3.4 遮挡根治技术方案（组合清单）

| 层 | 做法 | 为什么 |
|---|---|---|
| 路由层 | 对话页是 `MaterialApp` Navigator 上的独立 `MaterialPageRoute`，盖在 `RootShell`（含 `_BottomNav` 浮层）之上 | 该路由的 widget 树里**没有底部导航**——结构性消除"输入框被导航遮挡"，不再依赖 L90 的 viewInsets 隐藏 hack |
| Scaffold | `resizeToAvoidBottomInset: true`（默认即 true，显式写出防回退） | body 高度自动减去 `MediaQuery.viewInsets.bottom`，输入条贴键盘顶 |
| 背景 | `backgroundColor: BanxiaTokens.bg` **不透明** | RootShell 的 Scaffold 背景是 transparent（L31，为 Unity 画面透视）；二级页必须不透明，否则透过消息流看到 Unity 场景/下层标签页 |
| 顶部 | `AppBar` 自带状态栏避让；**不套** `SafeArea(top:true)`（旧 ChatScreen 的手动 SafeArea 删除） | 避免双重 top inset |
| 底部 | 仅输入条包 `SafeArea(top:false)` | 键盘收起时让开手势条；键盘弹出时 SafeArea 的 bottom padding 被 viewInsets 消费为 0，不会把输入条抬离键盘 |
| 消息流 | `Expanded` + `ListView`（`reverse` 不设，沿用现状首条在顶） | 键盘弹出时列表被压缩而非被键盘盖住；`_BubbleListState` 既有"新气泡滚到底"逻辑（L232-243）保留 |
| 手势条+键盘 | 不加任何 `AnimatedPadding(viewInsets)` 自实现 | Flutter 3.x 的 Scaffold inset 已处理键盘动画帧，手写 viewInsets 动画会与 resize 打架 |

**验证断言**（供实施后的模拟器盲测）：键盘弹出截图中，输入条底缘 y = 键盘顶缘 y ±2px；键盘收起截图中，发送钮中心 y ≥ 屏高 − (viewPadding.bottom + 23 + 12)。

### 3.5 未连接横幅（替代 `_GuideCard`）

```
Container(
  width: double.infinity,
  color: BanxiaTokens.orange.withOpacity(0.12),
  padding: EdgeInsets.symmetric(horizontal: 20, vertical: 10),
  child: Row(
    [ Icon(Icons.cloud_off, orange, 18) ]
    [ Expanded: '未连接后端，消息无法送达' 13 label ]
    [ GestureDetector '去绑定' 13 bold tint → pop + switchTab(settings) ]
  ),
)
```

横幅之下消息流与输入条**照常可用**（输入仍可点击，发送会被引擎拒绝并 toast——既有 `dispatch` 失败 toast 路径，无需新逻辑）。比整页引导卡更接近微信"断网仍可翻聊天记录"的体验。

### 3.6 消息气泡风格（沿用 + 增强）

| 项 | 现状（`chat_screen.dart` L244-274） | 设计定稿 |
|---|---|---|
| 对齐 | 用户右 / 角色左 `Align` | 不变 |
| 颜色 | 用户 `tintFill` 底白字；角色 `bgCard` 底 `label` 字 | 不变（iOS 蓝，与微信绿错位但更贴本 App 令牌体系） |
| 圆角 | 四角 20 | 微调为微信式**不对称**：用户侧 `BorderRadius.only(topLeft:20, topRight:20, bottomLeft:20, bottomRight:4)`；角色侧镜像（`bottomLeft:4`） |
| 最大宽 | `BoxConstraints(maxWidth: 280)` | 改为 `MediaQuery.sizeOf(context).width * 0.72`（280/390=0.72，令牌化比例，适配折叠屏/平板） |
| 时间戳 | 无（`ChatBubble` 只有 fromUser/text，`app_state.dart` L19-24） | v1：UI 侧记录收到时刻，**相邻气泡间隔 >5 分钟**插入居中时间头（11px `labelTertiary`，微信行为）；纯客户端派生，不动协议 |
| 发送状态 | 无 | v1 不做（协议无 ack 字段）；失败已由 `dispatch` toast 覆盖 |

### 3.7 Toast 的坑（必须处理）

全局 `_ToastOverlay` 在 `RootShell` 内部（`root_shell.dart` L38），被 push 上来的对话路由**盖住**——对话页里发送失败将看不到 toast。方案：对话页 `body` 外套 `Stack`，叠一个复用 `_ToastOverlay` 样式的页内 toast 层（同样监听 `appState.toast`，`IgnorePointer` + 底部 96px 胶囊）。样式常量与 L201-239 保持一致。

---

## 4. 迁移映射表

| 现有功能（`chat_screen.dart` 位置） | 新位置 | 处置 | 引擎接口 |
|---|---|---|---|
| `_NavBar` 大标题「对话」+ 连接徽章（L47-101） | AppBar 标题「伴夏」+ 副标题状态行；连接态并入副标题前缀点 | **合并**（标题降级为 17px；34px 大标题只属 tab 页） | 不变（读 `appState.connected`、`conversation.state/transportStatus`） |
| `_GuideCard` 未连接整页引导（L103-140） | 页顶 `_ConnectionBanner` | **改写**（整页卡 → 横幅；跳转从 `switchTab` 改为 pop+switchTab 组合） | 不变 |
| `_StatusCard` 头像+状态+麦克风三行（L142-208） | 首页入口卡（头像+名称）+ AppBar 副标题（状态行） | **拆分** | 不变 |
| `_BubbleList` 24 条气泡（L210-275） | 对话页 body 的 `Expanded` 槽 | **保留**（圆角/最大宽微调，§3.6） | 不变（`Evt.conversationTranscript/conversationReply`） |
| `_QuickPhrases` ≤3 条建议（L277-340） | 消息流与输入栏之间（原位） | **保留** | 不变（`Evt.conversationSuggestions` / `Cmd.conversationSend`） |
| `_ChatInputBar` 输入框（L393-406） | 输入栏首位 | **保留** | 不变 |
| 麦克风 `PopupMenuButton`（listen/record/restart/cancel，L410-462） | 输入栏第二位 | **保留** | 不变（`Cmd.voiceToggleListen/voiceToggleRecord/voiceRestart/voiceCancel`） |
| 相机发送钮（L464-479） | 输入栏第三位 | **保留** | 不变（`Cmd.conversationSendWithCamera`，受 `settings.camera` 门控） |
| 发送钮（L481-496） | 输入栏末位 | **保留** | 不变（`Cmd.conversationSend`） |
| 首页 `_QuickTiles`「对话」瓦片（`companion_screen.dart` L249-251） | 删除，由入口英雄卡替代 | **移动** | — |
| `AppTab.chat` 枚举与 IndexedStack 槽位 | 删除 | **删除**（导航层） | — |
| RootShell L90「键盘时隐藏底部导航」hack | 保留（设置二级页仍有输入场景? 否——其文本输入在二级路由内；tab 壳内已无输入框。可删） | **可删除**（简化，可选） | — |

**接口不变项汇总**：`Cmd.conversationSend`、`Cmd.conversationSendWithCamera`、`Cmd.conversationInterrupt`、`Cmd.voice*`、`Evt.conversationState/Transcript/Reply/Suggestions`、`Evt.voiceStatus`、`Evt.systemBack`、`Cmd.systemMinimize`——**零协议变更，纯 Flutter 壳层重组**。本次迁移天然满足双端同步原则（对话非设备独占），Quest 端 tab 结构是否跟进在 `PHONE_PORT_PLAN_CN.md` 登记"待同步"。

---

## 5. 视觉稿（390pt 参考画布，与 `BanxiaTokens` 注释同一基准）

### 5.1 首页（含入口英雄卡）

```
┌──────────────────────────────────────┐
│ ▓ 状态栏（系统）                      │
│                                      │
│  伴夏                    ← 34 bold   │  padding(20,16,20,12)
│  模型与入口               ← 15 labelSecondary
│                                      │
│  ┌────────────────────────────────┐  │
│  │ ○  和伴夏聊聊              ● › │  │  入口卡: bgCard, radiusCard 20
│  │    已连接 · 今晚想跳哪支舞？     │  │  头像 36 tintFill / 副标题 13
│  └────────────────────────────────┘  │  margin(20,0,20,10)
│  ┌──────────────┐ ┌────┐             │
│  │ 导入模型/动作 │ │刷新│  高48       │  _GlassButton 现状
│  └──────────────┘ └────┘             │
│  ┌────────────────────────────────┐  │
│  │ kokona          [使用中] 进入 删除│  _ModelCard 现状 radiusCard 20
│  └────────────────────────────────┘  │
│  快捷入口               ← 13 labelSecondary
│  ┌────────┐ ┌────────┐               │
│  │ 动作   │ │ 设置   │  高88 glass   │  「对话」瓦片已删
│  └────────┘ └────────┘               │
│  ┌────────┐ ┌────────┐               │
│  │ 更新   │ │        │               │
│  └────────┘ └────────┘               │
│        ┌──────────────────┐          │
│        │ 首页 │ 动作 │ 设置 │ 高64    │  悬浮胶囊 3 项, margin(20,0,20,20)
│        └──────────────────┘          │
└──────────────────────────────────────┘
```

### 5.2 对话页（键盘收起）

```
┌──────────────────────────────────────┐
│ ▓ 状态栏                              │
│  ‹       伴夏                  ← AppBar│  BackButton tint / 标题 17 bold 居中
│          idle · 麦克风待命     12px   │  副标题 labelSecondary
│ ──────────────────────────────────── │
│  未连接时在此出现橙色横幅（§3.5）       │
│                                      │
│  ┌─────────────────────╮             │
│  │ 晚上好呀，今天过得怎  ╲  角色左     │  bgCard, 四角20/左下4
│  ╰─────────────────────╯             │
│             ╭─────────────────────┐  │
│  用户右 →   ╱ 刚到家，有点累        │  │  tintFill 白字, 右下4
│             ╰─────────────────────┘  │
│             ╭─────────────────────┐  │
│             ╱ 那要不要先看会儿夜景？  │  │
│             ╰─────────────────────┘  │
│                                      │  ← Expanded 消息流, padding H20
│  ┌────────────────────────────────┐  │
│  │① 看夜景吧                      │  │  建议卡 ≤3, bgCard radius 12
│  ├────────────────────────────────┤  │  （空时整块隐藏）
│  │② 给我跳个舞提提神              │  │
│  └────────────────────────────────┘  │
│ ┌───────────────────────┐╭──╮╭──╮╭──╮ │
│ │ 输入消息…              ││🎤││📷││↑ │ │  输入条 padding(20,8,20,12)
│ └───────────────────────┘╰──╯╰──╯╰──╯ │  SafeArea(top:false) 包手势区
│ ▔▔▔ 手势条（系统）                     │
└──────────────────────────────────────┘
   ★ 本路由内无底部导航胶囊 ★
```

### 5.3 对话页（键盘弹出，贴键盘顶）

```
┌──────────────────────────────────────┐
│  ‹       伴夏                         │
│ ──────────────────────────────────── │
│  …（消息流被压缩，自动滚到底）          │
│             ╭─────────────────────┐  │
│             ╱ 那要不要先看会儿夜景？  │  │
│             ╰─────────────────────┘  │
│ ┌───────────────────────┐╭──╮╭──╮╭──╮ │
│ │ 好啊|                  ││🎤││📷││↑ │ │ ← 底缘 = 键盘顶缘 ±2px
│ └───────────────────────┘╰──╯╰──╯╰──╯ │   (resizeToAvoidBottomInset)
│ ┌──────────────────────────────────┐ │
│ │           系 统 键 盘             │ │  viewInsets.bottom
│ └──────────────────────────────────┘ │
└──────────────────────────────────────┘
```

### 5.4 令牌索引（实现时逐条对 `main.dart` L13-47）

| 用途 | 令牌 | 值 |
|---|---|---|
| 页面/聊天背景 | `BanxiaTokens.bg` | #F2F2F7 |
| 卡片/角色气泡 | `BanxiaTokens.bgCard` | #FFFFFF |
| 用户气泡/发送钮 | `BanxiaTokens.tintFill` | rgba(0,122,255,.9) |
| 返回键/链接 | `BanxiaTokens.tint` | #007AFF |
| 已连接点 | `BanxiaTokens.green` | #34C759 |
| 录音中 | `BanxiaTokens.red` | #FF3B30 |
| 未连接横幅 | `BanxiaTokens.orange` @12% | #FF9500 |
| 输入框底 | `BanxiaTokens.glass` | rgba(120,120,128,.12) |
| 主/副/三级文字 | `label / labelSecondary / labelTertiary` | — |
| 卡片/分组圆角 | `radiusCard 20 / radiusGroup 10` | — |
| 胶囊 | `radiusCapsule 999` | — |

---

## 6. 风险与备注

- **旧 `chat_screen.dart` 生命周期**：建议新 `chat_page.dart` 落地并从 root_shell/索引栈摘除后，同 commit 删除旧文件，避免两套对话 UI 漂移。
- **`handleSystemBack` 顺序已兼容**（pop 先于 tab 回落），但若未来对话页内再 push 三级页（如聊天记录搜索），无需改 back 逻辑，`canPop` 逐层退。
- **草稿保留**、**气泡时间戳的引擎侧真值**、**发送 ack 状态**列入 backlog（均需协议扩展，不在本期）。
- **Toast 双实例**：RootShell 与对话页各挂一层 toast overlay，同一 `appState.toast` 源，同一时刻只有顶层路由的可见，无重复感知问题。
