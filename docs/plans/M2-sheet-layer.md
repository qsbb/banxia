# M2 · 弹层状态机：遮罩 + 控件让位

> 变绿不变量：**INV-5（层级所有权）**
> 证据：截图 1 —— 「和她同框」弹层打开（y2135–3200 占 33% 屏），红色挂断键 y2728–2872 **叠压在弹层卡片文字上**，蓝色当前模式高亮与控件挤在一起
> 依赖：无（与 M1 可并行，但构建合并省机时建议串行）

---

## 0. 事实基线（已核实）

| 坐标 | 现状 |
|------|------|
| `BanxiaUiShell.ToggleCoPresenceSheet()`（L2316） | 切换模式/环境弹层的总入口 |
| `HideCoPresenceSheets()`（L2365） | 关闭所有弹层（选卡自动关已走这里） |
| `videoCallChrome`（L62） | `pickingMode=Ignore` 的通话 chrome 容器 |
| `.call-controls` | 挂断（红丸）+ 模式 + 环境 pill 行 |
| `coPresenceSheet` | 弹层本体（RebuildModeCards / RebuildEnvironmentChips 填充） |
| z 序现状 | videoCallChrome 先 Add、sheet 后 Add → sheet 画在上，但**控件未被隐藏**，视觉上红丸压卡 |

---

## 1. 设计规格（A1 公理：模态 = 所有权转移）

### 1.1 状态机

```
CallOnly ──ToggleCoPresenceSheet/模式pill/环境pill──▶ SheetModal
SheetModal 不变量：
  · callControls.style.display = None（不可见不可点，非半透明叠底）
  · 遮罩 call-scrim 显示：rgba(0,0,0,0.4)，盖住整个通话画面区
  · z 序：chrome(10) < scrim(20) < sheet(30) < toast(100)
SheetModal ──任一关闭路径──▶ CallOnly（控件 display 还原）
关闭路径三条（无死路）：
  ① 点遮罩任意处   ② 抓手下拉（grabber 拖拽超阈值）③ 选卡自动（既有）
```

### 1.2 视觉细化（全部复用既有令牌，零新增）

| 元素 | 规格 |
|------|------|
| `.call-scrim` | `position:absolute; top:0; left:0; right:0; bottom:0; background:rgba(0,0,0,0.4); pickingMode:Block`（必须 Block 拦截点击）|
| 弹层卡片当前态 | iOS 蓝 **2px 描边** + 右上 ✓（替代现在的大块蓝色高亮——蓝块在白弹层里太重）|
| 卡片行 | 高 190px、标题 34px/描述 26px 灰（--fs/--tint 既有）|
| sheet 顶角 | 上两角 48px、底部 0（贴屏底）|
| 淡入淡出 | 遮罩 opacity 120ms；sheet 平移 160ms ease-out（USS transition）|

---

## 2. 实施步骤（一次构建）

### 步骤 1 · USS：遮罩 + 弹层视觉
`Assets/UI/Resources/BanxiaTheme.uss` 追加：
```css
.call-scrim { position:absolute; top:0; left:0; right:0; bottom:0;
              background-color: rgba(0,0,0,0.4); transition-property: opacity; transition-duration: 0.12s; }
.cp-card.current { border-width: 2px; border-color: var(--tint); }   /* 替代蓝块 */
```
（`.cp-card.current` 现有蓝色底规则同步移除/覆盖，检查 BanxiaDsTheme.uss 的同名规则。）

### 步骤 2 · C#：遮罩元素创建与接线
`BanxiaUiShell.EnsureBuilt`（videoCallChrome Add 之后、coPresenceSheet Add 之前）：
```csharp
callScrim = new VisualElement { name = "call-scrim", pickingMode = PickingMode.Block };
callScrim.AddToClassList("call-scrim");
callScrim.style.display = DisplayStyle.None;
callScrim.RegisterCallback<ClickEvent>(_ => HideCoPresenceSheets());
shellRoot.Add(callScrim);            // Add 顺序即 z 序：chrome < scrim < sheet
```
toast 容器确认在 scrim 之后 Add（z=100 天然满足，检查现有 Add 顺序）。

### 步骤 3 · 打开路径改造
`ToggleCoPresenceSheet()` 及模式/环境 pill 的打开分支（L2270/2331/2353 三个调用点收敛到一个 `ShowCoPresenceSheet(sheet)`）：
```
ShowCoPresenceSheet(sheet):
  1. callControls（.call-controls 引用缓存）.style.display = None
  2. callScrim.style.display = Flex
  3. 目标 sheet display=Flex，另一个 sheet=None
```

### 步骤 4 · 关闭路径改造
`HideCoPresenceSheets()` 开头加：
```
callControls.style.display = Flex（还原）
callScrim.style.display = None
两个 sheet = None（既有）
```
`UpdateCoPresenceChrome()` 注意：它按 `inScene && videoCall` 重置 videoCallChrome 显示——补一行同步 scrim/控件状态，防止 HOME 恢复后 scrim 残留（对既有「HOME 丢 chrome」缺陷一并兜底）。

### 步骤 5 · 抓手下拉关闭
`RebuildModeCards/RebuildEnvironmentChips` 的 grabber（`.cp-grabber`）注册：
```
PointerDown 记 y0 → PointerMove Δ>120px 且向下 → HideCoPresenceSheets()
（简化版：grabber 区域点击即关，也满足验收 ② 的可达性——先实现点击关，拖拽作为增强）
```

### 步骤 6 · 构建与模拟器验证
```
tar BanxiaUiShell.cs + BanxiaTheme.uss（+DsTheme 若动）→ 5.55 构建
force-stop 冷启动 → 进场景（VideoCall）→ 模式 pill (540,2030)
→ 截图 A：assert-layer 断言（QA-assertions.md）
   · 控件带 y[2660,2900] 红色像素 = 0
   · 遮罩生效：背景亮度 ≤ 关闭态 60%
→ 点遮罩空白处 (540,1200) → 截图 B：控件回归（红丸像素 >0）+ sheet 关
→ 选卡路径：重开 sheet → 点虚拟场景卡 → toast「已切换」+ 控件回归
```
点击协议遵守 master §5.2（间隔 ≥4s，失效即 force-stop 重来）。

---

## 3. 验收清单

- [ ] INV-5：sheet 开启帧，`[2660,2900]` 挡断键带红色像素 = 0
- [ ] 遮罩下通话画面亮度 ≈ 原亮度 60%
- [ ] 三条关闭路径各自恢复控件、无 scrim 残留
- [ ] HOME 大法后 chrome/scrim 状态自洽（UpdateCoPresenceChrome 兜底）
- [ ] toast 出现在 sheet 之上
- [ ] 选卡切模式功能不回归（切虚拟场景后背景变色）

## 4. 回滚

单 commit（`fix(phone): M2 弹层状态机`）。

## 5. CLAUDE.md 合规

- 手机壳层（Quest 无触屏 sheet），不需双端同步；在 `PHONE_PORT_PLAN_CN.md` 备注「Quest 等价物=菜单面板层级规则，待统一」
- BOM / 零 vendored / 构建完告知 5.55 可关机
