# Banxia 补丁清单（vendored UnityMMDTools 本地修改记录）

> 维护纪律：本包为 vendored 依赖，任何本地修改必须在此登记（含动机、触点、上游冲突提示）。
> 上游：com.candidumgames.unitymmdtools（banxia 仓库内 vendored 副本）。

## [banxia] 2026-08-28 — M1 姿态源统一 + M3b deltaTime 防御

### 1. MMDPhysicsManager.cs — 0 子步帧姿态保持（pose-hold）

**动机**：物理固定步（30/60Hz）与显示刷新（72Hz）失配产生 0 子步帧。原实现
`TransformPhysicsInternal` 在 `elapsedTime<=0` 时只同步 Kinetic 刚体后直接 return，
模拟骨骼停留在动画层采样姿态 → 与步进帧的物理姿态**逐帧硬交替**（10-15Hz 闪烁，
抽搐主视觉贡献，见 banxia-physics-twitch-dev-plan.md Phase 1A）。

**改动**：
- `PhysicsSolverContext` 新增 `lastPhysicsLocalPositions/lastPhysicsLocalRotations/lastPhysicsPoseValid`
  （per-simulated-body 缓存，索引同 `sortedSimulatedRigidBodyIndices`）与
  `lastPoseSourceFlipCount/totalPoseSourceFlipFrames` 诊断计数。
- `ApplyDynamicRigidBodiesToBones` 写骨骼时同步缓存最后物理姿态（两个 Apply 助手改为
  `out` 返回应用值；`DynamicBoneAligned` 只缓存旋转——位置锁骨骼，回放不得冻结动画位移）。
- 新增 `ReplayLastPhysicsPoseToBones`（Burst）：0 子步帧将缓存姿态重写回骨骼；
  `Dynamic` 全量保持、`DynamicBoneAligned` 保持旋转+跟随当前动画位置。
- 新增 `HasPhysicsPoseDeviation`（位置 >1mm 或旋转 >~0.8° 判偏差）。
- `ReseedFromCurrentPose` / `ResetPhysics` / `RebuildRuntimeData` 同步失效/重置缓存，
  防止 reset 后回放陈旧姿态。
- 公开访问器 `lastPoseSourceFlipCount` / `totalPoseSourceFlipFrames`。

**验收指标**：`[Perf]` 心跳 `pose_src_flip`（累计 0 子步帧姿态偏差帧数）→ 稳态应为 0
（预热首几帧除外）。

### 2. MMDTransformManager.cs — 物理时间输入钳制（deltaTime guard）

**动机**：Quest3+Vulkan+OpenXR 下 `Time.deltaTime` 高方差/错误帧间隔
（Unity IssueTracker #7410 / N-127663），单帧污染即注入虚假追赶时间
（丢时 + HardSync 瞬移 + 动力学锚点过速度扫掠）。

**改动**：`LateUpdate` 的实时驱动路径改为
`TransformAll(Mathf.Clamp(Time.deltaTime, 0.005f, 0.04f), ...)`。
仅钳制实时路径；VMD 烘焙（显式固定 delta）不受影响。

### 升级冲突提示

- `TransformPhysicsInternal` 的 0 子步分支、`ApplyDynamicRigidBodiesToBones` 及两个
  `ApplyKinetic*` 助手签名已改（out 参数）——合并上游时需手工保留。
- `PhysicsSolverContext` 新字段参与 NativeArray 生命周期（RebuildRuntimeData 分配 /
  DisposePhysics 释放），合并时勿遗漏。

### 3. MMDPhysicsManager.cs — SetGroundCollisionEnabled 有效性防御（Phase 4a 配套）

原生上下文未构建时调用不再触达 Native（标志仍记录，`BuildGround` 在
（重）初始化时消费）。配合 banxia 侧在模型绑定时以 `SetGroundCollisionEnabled(false)`
关闭 y=0 无限地面碰撞（无动态体临近地面，纯省求解开销）。

### 4. MMDPhysicsManager.cs — 0 子步帧路径根治 + 渲染率姿态插值（Phase 5 / M4）

**动机**：原姿态保持补丁（M1）只挂在 `elapsedTime <= 0`（挂起/零时间）分支；
正常运行中累加器未满一个固定步的帧（30Hz 物理 / 55-72fps 渲染下约占 45%）
走 `StepSimulationWithKineticInterpolation` 返回 false 后**什么都不写**，
模拟骨骼仍显示动画层采样——闪烁主路径从未被覆盖，`pose_src_flip` 指标
因只在挂起路径计数而给出假阴性。此外 30Hz 物理下配饰姿态以阶梯更新，
与渲染率平滑移动的身体形成速率错配。

**改动**：
- `TransformPhysicsInternal`：步进返回 false（0 子步）时也调用
  `ReplayLastPhysicsPoseToBones`。
- 新增 `prevPhysicsLocalPositions/Rotations`（上一固定步姿态缓存）；
  步进帧 `ApplyDynamicRigidBodiesToBones` 与 0 子步帧回放统一写入
  `lerp(prev, last, timeAccumulator/h)` 插值姿态（标准固定步渲染插值，
  至多引入一步延迟）。`DynamicBoneAligned` 仅插值旋转，平移仍随动画。
- `ResolveInterpolationAlpha` 助手；`HasPhysicsPoseDeviation` 移除
  （插值是刻意偏离原始缓存姿态，偏差检查失去意义；`pose_src_flip`
  指标退化为仅计预热帧）。

**升级冲突提示**：`ApplyDynamicRigidBodiesToBones` /
`ReplayLastPhysicsPoseToBones` 已重写，`PhysicsSolverContext` 再增两个
NativeArray（ResizePersistent/Dispose 配对勿遗漏）。

### 5. MMDPhysicsManager.cs — 插值改世界空间（M4 修正）

**动机**：M4 在骨骼**局部**空间插值，对链式骨骼（披风 10+ 级）致命：
层级序处理中父骨骼先被改写为插值显示姿态，子骨骼的世界→局部换算
以父的插值矩阵为基准，误差逐级复合放大——披风飞起/拉伸变形。
（短链绒球/头发不明显，长链披风最夸张。）

**改动**：缓存改为骨骼**世界**姿态（`prev/lastPhysicsWorldPositions/
Rotations`）；显示姿态 = 世界空间 lerp(prev, last, α)，再对父骨骼
**已写入的显示矩阵**（层级序保证）换算回局部。两个合法物理状态之间
逐刚体世界插值，链内相对姿态保持关节约束，无复合误差。新增
`DecomposeRigid` 助手；局部空间缓存字段全部移除。

### 6. MMDPhysicsManager.cs + MMDTransformManager.cs — 根运动补偿（M6）

**动机**：角色根节点大幅移动（瞬移/放置重锚/追踪跳变）时，运动学刚体随骨骼
瞬间转移，而 Dynamic 刚体（披风/头发）被关节硬拽跨越位移——表现为披风
突然瞬移重新垂下。待机/满档重建日志排查排除后定位为此类。

**改动**：`MMDTransformManager.LateUpdate` 在 `TransformAll` 前调用
`physicsManager.CompensateRootMotion()`：根节点单帧位移 >0.15m 或旋转 >12°
时，将全部刚体、运动学插值基线（previous/currentKineticTargets）与姿态
缓存（prev/lastPhysicsWorld*）按同一世界刚体变换
`p' = rootNew + q*(p - rootOld), r' = q*r` 整体搬移（对刚性附着精确，
与真实支点无关）。平滑移动（~3cm/帧）与平滑转向（~2°/帧）低于阈值，
保留自然拖曳摆动。

### 7. MMDTransformManager.cs / MMDPhysicsManager.cs — deltaTime 门控 v2 + 吞姿态诊断（M7）

**动机**：心跳 `dropS=0.03`（恰=1 个固定步）反推出污染帧 dt≥36.4ms 真实到达；
旧钳制上限 40ms > 子步预算 33.3ms（cap/h），drop→HardSync 链路可达，
每次发生物理显示时钟永久落后动画 33ms 且伴随运动学锚点瞬移——
静止时局部抽搐的时序层嫌疑。另：重置回绑定姿态的判定容差
（|dot−1|≤1e-6 ≈ 0.16° 半角）可能吞掉小幅度脚本写入形成方波极限环，
需设备端数据裁决。

**改动**：
- `GateLiveDeltaTime`：5 帧中位数离群门（偏离 >1.8×+4ms 或 <0.5× 即视为
  污染帧，物理消费中位数而非封顶垃圾值）；钳制上限改为
  `maximumSubstepsPerFrame / simulationFrequencyHz`（两档均 33.3ms）——
  数学上保证累加器请求永不超过 cap，droppedSeconds ≡ 0。
- 吞姿态诊断计数器（`LastSwallowedPoseBoneCount`/`TotalSwallowedPoseFrames`）：
  Burst 重置前 managed 预扫描，采样姿态≈上次解算（将被吞回绑定）但离绑定
  >0.05° 的骨骼计数；待机时持续非零即实锤容差极限环。
- `MMDPhysicsManager.Initialize` 清零 timeAccumulator + 置
  resetKineticInterpolation（重建路径不带 Discard 的卫生漏洞）。

### 8. MMDBoneTransform.cs / MMDTransformManager.cs — 重置判定改精确写入追踪（M9）

**动机**：设备实测（M7 诊断指标）`swallow=89(累计数千)` 恒稳——重置回绑定姿态
的几何近似判定（`IsCurrentSolvedTransform`，|dot−1|≤1e-6 ≈ 0.16°）每帧吞掉
~89 根脚本驱动骨骼的刻意姿态（呼吸/摇摆小幅写入 + RestoreActionPose 的
flush 后硬写恰好≈解算值），物理层锚点与渲染层系统性不一致，边界骨在阈值上
翻转 → 腰部披风锚点被方波泵动（静止局部抽搐主因，设备数据实锤）。

**改动**：
- `SolverContext.externallyWrittenFlags`（NativeArray<bool>）：精确记录
  "上次 flush 之后有没有人写过这根骨"，取代几何猜测。
- `CaptureExternallyWrittenFlags`（ResetTransforms 前）：accumulate-only
  读 `Transform.hasChanged`；观察窗 = flush→reset。flush 自身写全部骨骼，
  故 `FlushBoneTransforms` 末尾同时清 hasChanged 与 flags。
- `solverResetPending` 骨在采样期强制 flags=true（保留原"不重置"语义）。
- `ResetRuntimeData(Internal)` 签名加 `externallyWritten`：written 骨保留
  采样姿态，untouched 骨照旧重置回绑定；`IsCurrentSolvedTransform` 退役
  （保留代码，不再被重置路径调用）。
- 初始/烘焙路径 flags 默认 true（保守：保留采样姿态）。

### 9. MMDPhysicsManager.cs — M6 越阈根运动后清速度（M11）

**动机**：显示路径盲测报告（4 路披风盲测之一）发现：M6 补偿把全部刚体+缓存
按根运动刚移，但 `SetRigidBodyTransforms(..., clearVelocity:false)` 不动 Bullet
速度 → 瞬移/急转/追踪跳变（>0.15m 或 >12°/帧）后，披风/头发动量仍指向旧世界
方向 → 一次整片甩动直到自然衰减。

**改动**：`ShiftAllBodiesBy` 中该调用改 `clearVelocity:true`。仅越阈帧到达此
路径（正常帧阈值门早退），速度清零使链条立即静止，杜绝甩动。位置/姿态仍按
q·(p−oldRoot)+newRoot 完整平移，插值缓存同步旋转。

### 10. 显示路径盲测结论存档（M11，未改码，备查）

4 路披风盲测之显示路径报告的已验证结论：
- M5 世界空间插值数学成立：显示位姿时间恒 = R−h（idle 33.3ms 常延迟），
  步进/0子步帧产出连续，无锯齿；"整片同相按 30Hz 节拍换向"是该设计固有形态。
- alpha 全局唯一（非逐骨）→ 无逐关节相位传播；如需布料传播感可做逐骨 α。
- RestoreActionPose(10400) 不直接写披风骨（12 骨集合，披风不在内）；
  间接影响=写祖先 upperBody/lowerBody 于 flush 后，动作期间披风整链承受
  一帧动作增量的刚性偏移（idle 稳态=0）。
- 档位切换重建 → warmup 1-2 帧绑定姿态（pose_src_flip 稳定 1-2 的来源）；
  SDEF 顶点蒙皮与 10400/10900 后的骨架有一帧相位差（装备一致性，观感项）。
- active 档 maxSubsteps=2 时 n=2 帧显示位姿时间回跳 ≈14-15ms（~5Hz 小抽动）；
  idle 档 cap=1 免疫。修法（如需）：子步位姿历史或批起点 α 重算。
- M6 越阈速度缺口已修（本文件 §9）。
