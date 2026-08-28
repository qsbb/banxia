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
