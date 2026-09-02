# QA 断言脚本集（INV-7 验证闭环）

> 位置：`/data/dsh/home/dsh/banxia/tools/qa/`（新建目录，随仓库提交）
> 运行环境：`/data/dsh/home/dsh/bridge-venv/bin/python` + PIL
> 统一约定：每个脚本 `exit 0` = 通过、`exit 1` = 失败；`--json` 输出机器可读结果；截图通过 NAS smbclient 拉取（凭据见 master §5.1）

---

## 0. 公共参数

| 参数 | 含义 | 默认 |
|------|------|------|
| `--screen-h` | 截图总高（自动读图亦可） | 自动 |
| `--top-px / --bottom-px` | 顶栏底缘 / 底控件顶缘（M1 叠加层红框亦会打印这些值；对用户的截图可手动传） | 330 / 2640（按 3200 屏比例换算）|

肤色掩膜（全脚本共用，来自本会话取证验证过的参数）：
`r>235 and 195≤g≤240 and 175≤b≤225 and r−b>25`

---

## 1. assert-framing.py（INV-1/2 · M1/ M4 真机）

```
用法：assert-framing.py 通话截图.png [--fullbody 全身截图.png]

A. 顶栏无脸：y∈[0, top_px] 肤色像素计数 == 0
B. 眼线带（可选，需叠加层十字标）：绿色十字标 y ∈ [0.28, 0.38]×screen_h
   （检测 #34C759 附近绿通道高亮像素的质心）
C. 填充率：y > 0.6×screen_h 区域非背景像素占比 > 30%
   （背景 = 截图四角主色，容差 ±30）
D. 全身模式：内容条带 top ∈ [8,12]%、bottom ∈ [88,92]%
输出：A/B/C/D 各 PASS/FAIL + 违规像素坐标样本
```

## 2. assert-layer.py（INV-5 · M2）

```
用法：assert-layer.py sheet开启截图.png [sheet关闭截图.png]

A. 控件让位：y∈[2660,2900]（按比例换算）红系像素（r>200,g<110,b<110）== 0
B. 遮罩压暗：给定关闭态截图时，同区域背景亮度 ≤ 关闭态 × 60%
C. 关闭态对照：关闭截图中红系像素 > 100（证明对照有效）
```

## 3. assert-shape.py（INV-3 · M3）

```
用法：assert-shape.py 键盘截图.png [--key-region x0 y0 x1 y1]

A. 键帽定位：该区域内灰度 ∈ [225,245] 的连通块 = 键帽包围盒（期望 12 个）
B. 边缘直线段：对每个键帽左缘逐行（步进 4px）取首个非白 x：
   存在 ≥8 个连续行 |x−中位x| ≤ 2px → 判圆角矩形
   全部行无直线段 → 判真椭圆 → FAIL
C. 尺寸一致性：12 个键帽宽高方差 < 5%（派生布局自证）
```

## 4. assert-align.py（INV-4 · M3）

```
用法：assert-align.py 键盘截图.png

对每个键帽：数字暗块（<150）质心 vs 键帽包围盒中心，偏差 < 8px
输出每键偏差与最大值
```

## 5. grep-radius.sh（INV-3 防复发 · 构建前置检查）

```
用法：tools/qa/grep-radius.sh   （在 banxia 仓库根运行）

rg -n 'border-radius:\s*9[0-9]{2,}px' Assets/UI/ → 期望 0 行
再列出全部 border-radius 行，附「半径 vs 同规则元素高度」提示表供人工核对
exit 1 = 存在违规绝对大半径
```

## 6. assert-overlay.py（INV-7 · M1 模拟器自证）

```
用法：assert-overlay.py HUD网格开启截图.png

A. 红框安全区存在（红系像素构成的矩形边框带）
B. 绿 1/3 线存在（绿色横向直线带）
C. 左上角数值区存在（深色小字块）
→ 证明叠加层渲染成功 = 构图可观测性成立
```

---

## 7. 脚本落库与 CI 式使用

1. M1 构建前：先落 `assert-overlay.py / assert-framing.py`（分析用例直接迁移本会话已验证的像素逻辑）
2. 每里程碑装机截图 → 对应脚本跑 → 结果贴回该里程碑 md 的验收清单
3. M4 步骤 6 用户复拍截图 = 最终全脚本回归
4. 脚本自身改动随对应里程碑 commit（`test(qa): …`）

## 8. 边界与诚实声明

- 模拟器无 PMX 模型且 fallback 不生成 → assert-framing 的 A/C 项**只在用户真机截图上有意义**；模拟器只跑 assert-overlay + logcat 求解行断言
- B 项依赖叠加层十字标，若用户未开 HUD 则跳过并警告（不 FAIL）
- 肤色掩膜对深肤色/逆光截图可能漏检 → FAIL 时输出样本坐标供人工复核，不盲目信
