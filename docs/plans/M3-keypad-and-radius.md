# M3 · 配对键盘派生重排 + 全仓圆角体检

> 变绿不变量：**INV-3（形状派生）· INV-4（内容对齐）**
> 证据：截图 3 —— 「连接后端」页 12 个键帽（2 列×6 行塌陷）为**真椭圆**（左缘描边 x: 462→435→497 连续弯曲、无直线段）；数字 60px 偏小且**偏左上约 50px 未居中**；键盘仅占屏中 1/3
> 事实修正：键帽实为 `.numpad-key`（190×150, radius 75 = h/2 ✓ 胶囊值本身合规），**椭圆观感 = flex-wrap 在 190+28×2 宽度下每行只容 2 键 + 视觉放大**；数字偏移 = `.numpad-key-label` 无 `-unity-text-align`
> 依赖：无（建议与 M2 合并一次构建省机时）

---

## 0. 事实基线（已核实）

| 坐标 | 现状 |
|------|------|
| `BuildPairingNumpad(parent)`（BanxiaUiShell L1061） | 12 键：1–9 + 清/0/退，flex-wrap 塞进 `.numpad` |
| `MakeNumpadKey(text, click)`（L2705） | VisualElement + `.numpad-key` + Label `.numpad-key-label` + ClickEvent |
| `.numpad` USS | `flex-direction:row; flex-wrap:wrap; justify-content:center; margin:20px 120px` |
| `.numpad-key` USS | `width:190px; height:150px; border-radius:75px`（=h/2 ✓）|
| `.numpad-key-label` USS | `font-size:60px`，**无 -unity-text-align**（默认 upper-left = 偏左上根因）|
| `.code-dot` | 38px 圆点 ×6，正常 |
| 键盘位置 | 对话页 `chatPairingCard`（未连接时显示；设置连接页是另一份表单）|

---

## 1. 设计规格（全部派生，零魔法数）

### 1.1 几何派生链

```
键高   H = Screen.height / 16          （3200→200px；2340→146px，均 ≥ 9mm 触摸下限）
键宽   W = (卡内宽 − 2×边距 − 2×列距) / 3
字号   F = 0.42 × H                     （数字键盘通行比例）
半径   R = H / 2（精确值，INV-3 胶囊判例）
行距   = 0.16 × H；列距 = 0.12 × W；边距 = 24px（卡片内边距，非键帽数）
```
运行时由 C# 读 `Screen.height` 换算 panel 像素（装机后用 logcat 打印验证 1:1，见步骤 2b）。

### 1.2 布局与键序（3×4，iOS 电话键盘范式）

```
[ 1 ][ 2 ][ 3 ]
[ 4 ][ 5 ][ 6 ]
[ 7 ][ 8 ][ 9 ]
[ ⌫ ][ 0 ][ ✓ ]
```
- `清`（清空）并入 **⌫ 长按**（PointerDown 计时 ≥600ms 触发 ClearPairingCode；短按 = RemovePairingDigit 退格）
- `✓` = 提交（调既有 `TryPair()`；码不足 6 位时复用其 toast「请输入完整的 6 位配对码」）
- 派生布局使卡片高度 ~1600px → ~1100px，页面对话区上移，不再空旷
- **Flutter 实现（2026-09-02）**：本布局已落为双端共享的
  `flutter_ui/lib/scene/pairing_numpad.dart`，保留 6 位「清/0/退」语义——清 = ⌫ 长按
  (≥600ms)、0 = 0 键、退 = ⌫ 短按；✓ 为**独立显式提交校验**（AppState 校验满 6 位，
  不足走 toast「请输入完整的 6 位配对码」，再 `pairing.pair` → 引擎 `PairWithCode`），
  语义标签「退格/确定」暴露给无障碍。Quest 端经纹理/世界面板呈现（离屏渲染未实现，
  见 PHONE_PORT_PLAN_CN.md §3.6）。

### 1.3 键帽状态（令牌复用）

| 态 | 值 |
|----|-----|
| 底色 | `var(--glass)`（既有）|
| 边 | 现有 2px 玻璃边三向（保留）|
| 按下 | `--glass-pressed` + `scale 0.97`（既有 :active ✓）|
| 字色 | `var(--label)` |

---

## 2. 实施步骤（一次构建）

### 步骤 1 · C# `BuildPairingNumpad` 重排
文件 `Assets/Scripts/UI/BanxiaUiShell.cs`：
```
1. 删除 12 次 MakeNumpadKey 的 flex-wrap 塞法，改为显式 4 行：
   rows = ["123","456","789","⌫0✓"]
2. 每行一个 row 容器（flex-direction:row; justify-content:space-between）
3. MakeNumpadKey(text, click, keyH) 增参：
   style.width  = W（由父行宽派生：行宽 = 卡内宽−2×24，W=(行宽−2×0.12W)/3 → 直接 W=行宽*0.31，微调列距为 margin）
   style.height = keyH；style.borderRadius = keyH/2（精确值， INV-3）
4. Label：style.fontSize = 0.42f*keyH；style.unityTextAlign = MiddleCenter
5. ⌫ 长按：RegisterCallback<PointerDownEvent> 记时间 + PointerUpEvent 判 ≥600ms → 清空
   （长按期间可加 scale 0.97 反馈；简单实现即可）
```

### 步骤 2b · panel 像素与 Screen 像素 1:1 验证
`BuildPairingNumpad` 开头 `Debug.Log($"[M3] screenH={Screen.height} panelH={root.panel?.visualTree?.resolvedStyle.height}")` —— 装机 logcat 核对；若不等比，派生改用 panel 高。

### 步骤 3 · USS 修订
`Assets/UI/Resources/BanxiaTheme.uss`：
```css
.numpad { flex-direction: column; align-items: stretch; margin: 20px 24px; }
.numpad-row { flex-direction: row; justify-content: space-between; margin: 0 0 12px 0; }
.numpad-key { /* 删 width/height/radius 硬值 → 由 C# 派生注入 style */ }
.numpad-key-label { -unity-text-align: middle-center; }
```
（保留 :active / 玻璃边 / transition。）

### 步骤 4 · 全仓圆角体检（INV-3 防复发）
```
grep -rn "border-radius:\s*9[0-9]\{2,\}px" Assets/UI/   → 期望 0 处
grep -rn "border-radius" Assets/UI/ | 人工核对清单：
  每条半径 vs 元素高度（USS 内同规则）：胶囊必须 = h/2；卡片 ≤ 48px
产出体检表（文件→规则→判定）附在本文件末尾附录 A
```
BanxiaDsTheme.uss 若有 >48px 的卡片类半径，收敛为令牌 `--radius-card: 48px`（令牌登记，零新增色值）。

### 步骤 5 · 构建与模拟器验证
```
tar（BanxiaUiShell.cs + BanxiaTheme.uss [+ DsTheme]）→ 5.55 构建
force-stop 冷启动 26s → 对话页（未连接时 chatPairingCard 显示 numpad）
→ 截图 → tools/qa/assert-shape.py：
   · 键帽左缘描边：中段存在 ≥8 行恒 x 直线段（±2px）
→ tools/qa/assert-align.py：
   · 数字字块质心 vs 键帽包围盒中心偏差 <8px
→ logcat [M3] screenH/panelH 行核对
→ 功能回归：依次点 1/2/3…⌫/✓，pairingCode 长度与 code-dot 填充数一致（截图点数）
```

---

## 3. 验收清单

- [ ] INV-3：assert-shape.py 通过（直线段存在 = 圆角矩形非椭圆）
- [ ] INV-3：grep 9xx px = 0 处；体检表全部「=h/2 或 ≤48」
- [ ] INV-4：assert-align.py 通过（<8px）
- [ ] 布局：3 列等宽、4 行、卡片高度降至 ~1100px、左右边距对称
- [ ] 功能：数字追加/退格/长按清空/✓ 提交 toast 全通；code-dot 同步
- [ ] 分辨率自适应：模拟器 1080×2340 与用户 1440×3200 双验证（键高 S/16 生效）

## 4. 回滚

单 commit（`fix(phone): M3 键盘派生重排+圆角体检`）。

## 5. CLAUDE.md 合规

- 配对 numpad 已随 Flutter 壳层迁为**双端共享**（`flutter_ui/lib/scene/pairing_numpad.dart`，保留 6 位清/0/退语义 + 显式 ✓ 提交校验）；原「手机壳层不同步」理由失效，已在 `PHONE_PORT_PLAN_CN.md` 待同步清单更新
- BOM / 零 vendored / 构建完告知 5.55 可关机

## 附录 A · 圆角体检表（步骤 4 产出后回填）

| 文件 | 规则 | 半径 | 元素高 | 判定 |
|------|------|------|--------|------|
| BanxiaDsTheme.uss | （回填） | | | |
| BanxiaTheme.uss | （回填） | | | |
