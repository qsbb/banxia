# M1 · 通话构图闭式解 + QA 叠加层

> 变绿不变量：**INV-1（脸部可见）· INV-2（画面填充）· INV-7（构图可观测）**
> 证据：截图 1/2 —— 脸被 `.call-top` 毛玻璃条遮死（面板中央透出肤色）、脚底=屏幕 49%、下半屏全空
> 依赖：无（可独立开工）

---

## 0. 事实基线（已核实代码坐标）

| 坐标 | 内容 |
|------|------|
| `PhoneCoPresenceDirector.EnterVideoCall()` | 现为 bounds 72% 胸高 + 距离 0.85h + yaw 归零（commit 80862b2）——即截图里仍在失败的那版 |
| `AvatarController.cs` L89 `private Transform head;` | 头骨骼已有，但 private 无对外暴露 |
| `PhoneOrbitCamera.ComputeRenderBounds` | 已 public static（80862b2 改的） |
| `BanxiaUiShell` → `videoCallChrome` 内 `.call-top` | 顶栏（伴夏+计时+字幕），即挡脸者 |
| `BanxiaUiShell` → `.call-controls` | 底部挂断/模式/环境行 |
| `mainCamera.fieldOfView` | Unity 垂直 FOV（竖屏沿长边），运行时实测确认 |

---

## 1. 设计规格（闭式解）

### 1.1 语义输入（三级锚点链）

```
E  (眼线)  = 头骨骼世界位置 + 0.03m          ← 优先级 1（A3 语义锚点）
H_head     = 0.20m（命名语义常数：MMD 头高）
headTop    = E + 0.10m（半头高）
C_waist    = E − 0.44m（= 2.2 × H_head，命名语义常数）
C_chest    = E − 0.30m（= 1.5 × H_head）
feet       = 渲染包围盒 min.y（脚在包围盒里可靠）
锚点缺失（无头骨骼）→ bounds 退化：E = bounds.min.y + 0.83 × bounds.h，
  距离夹上限保守 1.6m，并强制走 §5 叠加层人工核验
```

### 1.2 实测量（运行时，禁止假设）

```
S = Screen.height（px）          θ = mainCamera.fieldOfView（度）
T = 顶栏底缘 y（.call-top 世界包围盒 yMax，px）
B = 底控件顶缘 y（.call-controls yMin，px）
k = (S/2) / tan(θ/2)
```

### 1.3 求解（pitch=0，人物近似共面，视差≤0.1m 忽略）

**胸像（视频通话）**：

```
s_E = T + (B−T)/3          眼线 = 可视区上 1/3 线
s_C = B                    腰线切底
d   = k · (E − C_waist) / (s_C − s_E)
h   = E + (s_E − S/2) · d / k      相机高度
d 夹到 [0.55, 2.4]；取上限时降级为腰线→胸口局部构图并记 warning
```

**全身像（虚拟场景，FrameModel 同步升级）**：

```
s_head = 0.08·S；s_feet = 0.92·S
d   = k · (headTop − feet) / (0.84·S)
h   = (headTop + feet)/2
```

**工作示例**（S=3200, θ=60°→k≈2771, T=330, B=2640，已验算）：
胸像 0.44m 跨度 → d≈0.79m ✓；全身 1.6m → d≈1.97m ✓；纯胸口切底 0.30m → d=0.54m ✗（透视畸变，故定腰线切底）。

### 1.4 常数登记表（A2：仅语义常数合法）

| 常数 | 值 | 语义 |
|------|-----|------|
| `EYE_OFFSET` | 0.03m | 眼线在头骨上方 |
| `HEAD_HEIGHT` | 0.20m | MMD 头高 |
| `EYE_TO_WAIST` | 2.2×HEAD | 眼到腰 |
| `EYE_TO_CHEST` | 1.5×HEAD | 眼到胸 |
| `FRAME_BAND` | [8%, 92%] | 全身像上下安全带 |
| `EYE_LINE_RATIO` | 1/3 | 三分法眼线 |
| `DIST_CLAMP` | [0.55, 2.4] | 相机距离 |

---

## 2. 实施步骤（一次构建）

### 步骤 1 · `AvatarController` 暴露头骨
文件 `Assets/Scripts/Core/AvatarController.cs`，在 `public Transform VisualRoot`（L160 附近）旁加：
```csharp
/// <summary>头骨骼（构图语义锚点）；模型未加载或无头骨时为 null。</summary>
public Transform HeadBone => head;
```
BOM 检查（脚本补 BOM 后 `head -c3` 验证）。

### 步骤 2 · 新建平台无关求解器
新文件 `Assets/Scripts/Core/CallFramingSolver.cs`（UTF-8 BOM，无 `#if`，纯静态）：
```csharp
public static class CallFramingSolver
{
    public struct Inputs { public float S, ThetaDeg, TopPx, BottomPx;      // 实测
                           public float EyeY, HeadTopY, FootY, LowCutY; }  // 语义锚
    public struct Result  { public float Distance, CameraY; }
    public static Result SolveBust(in Inputs i);     // 眼1/3线+腰线切底
    public static Result SolveFullBody(in Inputs i); // 8–92% 带
}
```
两函数按 §1.3 公式实现；`SolveBust` 内含距离夹取与降级分支。

### 步骤 3 · chrome 内测注入
`BanxiaUiShell`：`EnsureBuilt` 完成后（及 `videoCallChrome` 可见时）量取 `.call-top`、`.call-controls` 的 `worldBound`，调 `owner.CoPresence.SetChromeInsets(topPx, bottomPx)`。面板 GeometryChangedEvent（分辨率变化）时重发一次。
`PhoneCoPresenceDirector` 新增 `public void SetChromeInsets(float top, float bottom)`（存字段）。

### 步骤 4 · `EnterVideoCall` 重写
替换 80862b2 的 bounds 块（保留 yaw 归零 + 面向相机段）：
```
1. 取 AvatarController = avatarRoot.GetComponent<AvatarController>()
2. 锚点链：HeadBone≠null → §1.1 语义值；否则 bounds 退化（记 Debug.LogWarning 一次）
3. S/θ 用 mainCamera.pixelHeight / fieldOfView；T/B 用注入的 chrome insets（未注入时 T=0.03S、B=0.88S 兜底并告警）
4. SolveBust → orbitCamera.SetOrbitTarget(new V3(x, E, z)) + OrbitDistance=d + 相机 y
   （OrbitDistance setter 只控距离；相机 y 由 orbitTarget+pitch=0 推出，target y 取 h 对应值）
5. Debug.Log 求解值（d/h/E/s_E，一行）——供模拟器断言
```

### 步骤 5 · `FrameModel` 全身像升级
`PhoneOrbitCamera.FrameModel`：headTop/feet 锚点齐全时走 `SolveFullBody`；否则保留现行 bounds 逻辑。

### 步骤 6 · QA 构图叠加层
`PhoneDiagnosticsHud`（既有诊断 HUD，设置页有开关）新增「构图网格」开关，开启时用 `IMGUI`/UI Toolkit 画：
- 红框：`[0,T]` 与 `[B,S]` 安全区矩形
- 绿虚线：1/3 线、7/10 线
- 十字标：headTop / 眼线 / 腰线 / feet 的实时屏幕投影（从 director 拿锚点世界坐标，`camera.WorldToScreenPoint`）
- 左上角数字：`d=… h=… eye%=… anchor=head|bounds`
发布构建默认关（跟随 HUD 开关，无需剔除代码）。

### 步骤 7 · 构建与模拟器验证
```
tar 三文件+USS → 5.55 → touch → del apk → wait.ps1 → sleep 570 → dir 时间戳
装机（force-stop 冷启动 26s）→ 进场景（默认模式记忆 VideoCall）
→ logcat grep 求解行：读 d/h/eye% 三值
→ 开 HUD 构图网格（设置→通用→HUD；网格开关）→ 截图
→ 断言：绿 1/3 线存在 + 十字标眼线落在绿带内（叠加层自证）
```

### 步骤 8 · 真机验收（用户）
用户装新包 → 开 HUD 构图网格 → 视频通话截 1 张 + 虚拟场景截 1 张发 NAS `文件传输/`。
本机跑 `tools/qa/assert-framing.py`（见 QA-assertions.md）对两张截图断言。

---

## 3. 验收清单

- [ ] INV-1：顶栏区 [0, T] 肤色像素 = 0（assert-framing.py A 项）
- [ ] INV-1：眼线（十字标）∈ 可视区 28–38%
- [ ] INV-2：通话 y>0.6S 区域人物覆盖 >30%；全身像头 ∈ [8,12]%、脚 ∈ [88,92]%
- [ ] INV-7：logcat 求解行存在；锚点类型显示（head / bounds）
- [ ] bounds 退化路径单独验证一次（临时禁用头骨 accessor 或用 fallback 场景）
- [ ] 模拟器全链路 force-stop 冷启动通过；构建完告知 5.55 可关机

## 4. 回滚

单 commit（`fix(phone): M1 闭式构图`），revert 即回到 80862b2 构图。

## 5. CLAUDE.md 合规

- 求解器平台无关（无 `#if BANXIA_PHONE`），双端可复用；完成后在 `PHONE_PORT_PLAN_CN.md` 更新「Quest 端三模式构图同步（复用 CallFramingSolver）」待同步条目
- .cs 全部 UTF-8 BOM；vendored 零改动；构建验证前不宣称完成
