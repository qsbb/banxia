# 手机端「模型站屏幕中间 / 返回键无效」排查报告（2026-09-07）

测试环境：Android 模拟器（emulator-5554），包 `com.lingxi.banxia.phone`，
版本 `0.3.2.20260906`（f49600d，含 debug 模式）。模型：真实 PMX「xinxia」
（休日冒险 / 裸足两个变体均复现）。方法：adb 截屏逐像素分析 + logcat 日志
+ 求解器/世界坐标换算（相机 FOV=60°，k=2026px/m·rad 已用地面远边验证）。

## TL;DR

1. **模型站位问题的根因不在构图求解器**：求解器、相机、骨骼、包围盒全部
   正常，但**渲染出来的蒙皮网格被系统性纵向压缩**：
   `渲染世界高度 y′ ≈ 0.507 × 绑定高度 y + 0.92`（仅 Y 轴，水平宽度正常）。
   等效于以 y≈1.86m 为轴心的 ~0.5 倍压缩：脚底悬空 0.92m、头顶只高出
   0.1m、模型渲染身高只有骨骼身高的约一半 → 视觉上"整个人缩在屏幕中间、
   脚不沾地"。上一版（e0168a9）只修了求解器，所以"还是"站在中间。
2. **返回键无效根因已定位**：Flutter 面板窗口持有按键焦点，Flutter 侧
   没有任何系统返回处理，Java 宿主也不拦截 KEYCODE_BACK。
3. 连带发现 5 项，见文末维修清单。

## 一、站位问题证据链

| 层 | 读数 | 结论 |
|---|---|---|
| 相机 | FrameModel：d=1.704、h=0.901、pitch=0；地面远边实测 y=1207 vs 理论 1201 | ✅ 正确 |
| 求解器 | 半身模式 solve：d=0.806、h=1.377、sE=994=设计值 | ✅ 正确 |
| 骨骼 | 头骨 y=1.417m（E=1.447−EyeOffset 0.03），1.65m 模型正常值 | ✅ 正常 |
| 包围盒 | 恒为绑定姿态 1.62–1.65m | ✅ 正常（但见第 4 点） |
| **渲染** | 头顶 y≈1.754（应 1.649）、脚底 y≈0.918（应 0）、半身模式脸部 y≈1.66–1.75（骨骼眼位 1.42–1.45） | ❌ **压缩** |

四点（头顶/脸/裙摆/脚底）在两个模型、全身与半身两种取景下均精确命中
`y′=0.507y+0.92`，排除偶然。水平宽度正常（发带渲染 0.37m ↔ PMX 0.30m
±摆动，相机距离 1.704 自洽）——排除"相机太远/整体缩放"。

关键辅助事实：

4. **包围盒不能证明姿态正常**：`PMXRendererBuilder` 设置
   `renderer.localBounds = mesh.bounds`（静态绑定姿态 AABB），Unity 对蒙皮
   渲染器用 rootBone 变换该静态盒，**永不反映当前姿态**——所以 FrameModel
   一直读到 1.65m，这也是此前"包围盒正常"误导排查的原因。
5. **从第一帧即如此**：加载后 +3s 截屏头部已在压缩高度（此时裙摆还在
   物理爆炸，见维修清单 4）。
6. 与物理档位无关（平衡 30Hz / 性能 60Hz 渲染一致）；与模型文件无关
   （两个独立解析的变体同现）。
7. 静态网格（地板 Plane）渲染完全正常 → 发散只发生在**蒙皮网格**。
8. `gpuSkinning=0`（CPU 蒙皮），排除 GPU 蒙皮驱动 bug；模型权重只有
   BDEF1/BDEF2，无 SDEF，排除 SDEF 路径；项目未装 lilToon，材质走
   URP Unlit 原生 shader，排除自定义顶点着色器。
9. AvatarController 的头骨就是 UMT 蒙皮骨骼本身（FindBone 直接搜
   MMDBoneTransform 组件），不存在双套骨骼。

### 悖论与根因候选

在标准 Unity 蒙皮下 `顶点 = 骨骼世界矩阵 × bindpose × 网格顶点`，骨骼正常
则渲染必然正常；bindpose 构造（`bone.worldToLocal × root.localToWorld`）
对根变换不变。骨骼正常 + 渲染压缩在数学上不可能同时成立——除非蒙皮输出
被人为改写。剩余候选机制（按嫌疑排序）：

- **A. 运行时改写蒙皮网格顶点**：某组件以 CPU 方式写 `mesh.vertices`
  （SDEF skinner 有 `MarkDynamic` 运行时网格，但本模型无 SDEF 权重；
  需排查是否仍有其他运行时网格写路径被激活）。
- **B. BlendShape/表情路径把全局位移写进顶点**（表情=默认状态下是否
  有非零权重形态键，PMX morph 数据未解析过，未知）。
- **C. 蒙皮矩阵被中间层替换**（MMDTransformManager 的 FlushBoneTransforms
  之外的第二条骨骼写路径，或渲染器 bones 数组被重绑）。

注：VMD 驱动实验（合成 VMD 抬升 センター 0.64m）因"进场景必重载模型、
重载必杀播放"（`VmdActionLibrary.ClearModel→CompleteReturnToIdle`）无法
在可视状态下完成，已记录为测试设施缺口。

## 二、返回键无效根因

- `dumpsys window`：Flutter Panel 窗口全屏、可触摸、持 `mCurrentFocus`，
  系统返回键事件投递给 FlutterView。
- Flutter 侧：全工程**没有** PopScope / onPopInvoked / 路由 pop 处理，
  仅设置页有一个视觉上的 BackButton 组件（app_bar 返回箭头，可用）。
  → 系统返回键在 Flutter 框架内无人消费，静默吞掉。
- Java 宿主（banxia_flutter.androidlib）：无 onBackPressed /
  dispatchKeyEvent / KEYCODE_BACK 任何覆盖。
- UnityPlayerActivity 在面板之下，拿不到按键。
- 实测：连按两次返回，弹层不关、模式不退、页面不动，应用存活。

**修复方向**：Flutter 顶层 `PopScope`（场景模式→回主界面；弹层开→关弹层；
主界面→让系统处理）+ Java 侧兜底转发；同时把"返回键语义表"写进
QA-assertions。

## 三、场景触摸完全失效（新发现，重大）

- 面板窗口无 `FLAG_NOT_TOUCHABLE`，**吃掉全部触摸**；Unity 侧实测
  零触摸到达（PhoneOrbitCamera 的拖拽旋转/双指缩放/双击重置/位移全部
  失效；AvatarTouchInteraction 的触摸交互同样失效）。
- 目前场景内只有 Flutter 胶囊按钮（取景/HUD/主界面/模式/环境）可用——
  这些走桥接命令，不依赖 Unity 触摸。
- 修复方向：SceneOverlay 背景加手势层，把单指拖动/双指缩放/双击翻译为
  桥命令（`copresence.moveAvatar` / `orbit.*` / `reframe`），或在原生层把
  未命中触摸转发给 Unity。手势优先（纯 Dart，不动原生）。

## 四、维修清单（按优先级）

| # | 问题 | 根因状态 | 建议 |
|---|---|---|---|
| 1 | **蒙皮渲染纵向压缩 0.5×+0.92m（站位问题本体）** | 发散点已锁定在"骨骼→蒙皮顶点"之间，机制待仪器确认 | 加 QA 探针后定点修复（见下） |
| 2 | 返回键全局无效 | ✅ 已定位 | Flutter PopScope + Java 兜底 |
| 3 | 场景内 Unity 触摸全灭 | ✅ 已定位 | Flutter 手势→桥命令 |
| 4 | 物理：生成瞬间裙摆爆炸（+3s 截屏见大范围对角拖尾）；`pose_src_flip` 在 60Hz 档下 ~19/s 持续上涨（姿态缓存持续失效） | 未定位；与 #1 是否同源待查 | 探针顺带记录物理体初始世界位置 |
| 5 | 构图网格/诊断 HUD 设置不跨启动恢复 | ✅ 已定位：Flutter 侧设置无持久化（默认 framingGrid=false），Unity PlayerPrefs 存了但 Flutter 路径启动不读（FlutterUiFacade 无恢复逻辑） | Flutter 侧持久化 + 启动时下发 |
| 6 | 底部胶囊/导航无障碍边界 [0,0][0,0]（TalkBack 失效；点击正常） | 面板窗口 a11y 视口（[0,136][1080,2208]）与实际全屏绘制不一致 | 低优先 |
| 7 | VMD 播放前同步转换 19–21s（一个 4 帧 VMD 烘焙出 228387 keys / 387 条曲线），期间 UI 无反馈；进场景必重载模型并杀掉播放 | ✅ 机制清楚 | 转换异步化+进度；动作页内直接预览 |
| 8 | 单次未复现冻结（04:05–04:23 全屏像素级静止，EGL 间隔 216s/639s） | 未复现，疑似测试序列诱发 | 观察 |

## 五、下一步（#1 的定点方案）

加一个 QA 诊断命令（debug 模式下触发），输出：

1. `SkinnedMeshRenderer.BakeMesh()` 后的**真实蒙皮 AABB** vs 骨骼链
   AABB vs `renderer.bounds`——一次调用即可判定发散在"蒙皮矩阵"还是
   "绘制矩阵"。
2. 逐骨骼 dump：`bone.worldMatrix` 与 `bindpose` 的乘积对照（头/腰/脚）。
3. 每个 renderer 的 `bones[i]` 与 AvatarController 同名骨骼的实例
   同一性（排除任何重绑）。
4. 物理体初始世界位置（排查 #4 是否同源）。

探针结果出来后按图索骥修（大概率落在 vendored UMT 包，需单独 commit +
`BANXIA-PATCHES.md` 登记）。修完后用本次建立的像素测量法回归验证：
脚底应落在 y≈2027±40px、头顶 y≈155±40px（d=1.704 全身取景）。
