using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 蒙皮发散诊断探针（2026-09 手机端「模型站屏幕中间」排查）。
    ///
    /// 现象：CPU 侧骨骼/包围盒/求解器全部正常，但渲染出的蒙皮网格被纵向压缩
    /// （实测 y'≈0.507y+0.92m）。本探针一次性输出三层对照数据到 logcat
    /// （[SkinAudit] 前缀），用于把发散点钉死在「骨骼 Transform → 蒙皮矩阵 →
    /// 绘制」链条的某一环：
    ///   1. 关键骨骼的世界 Y（CPU 场景图真值）；
    ///   2. 每个 SkinnedMeshRenderer 的静态包围盒 / renderer.bounds /
    ///      烘焙后真实蒙皮 AABB（BakeMesh = 蒙皮输出真值）；
    ///   3. 渲染器节点自身变换、rootBone、bones 数组与 AvatarController 骨骼的
    ///      实例同一性、非零 BlendShape 权重。
    /// 若烘焙 AABB ≈ 骨骼范围 → 发散在绘制矩阵；若烘焙 AABB 已被压缩 → 发散在
    /// 蒙皮矩阵（bindpose/骨骼数组）。
    /// </summary>
    public static class AvatarSkinAudit
    {
        private static readonly string[] KeyBoneNames =
        {
            "全ての親", "センター", "下半身", "上半身", "頭", "左足首", "右足首"
        };

        public static void Run(AvatarController avatar)
        {
            if (avatar == null)
            {
                Debug.LogWarning("[SkinAudit] avatar=null");
                return;
            }
            var root = avatar.transform;
            var visualRoot = avatar.VisualRoot;
            Debug.Log("[SkinAudit] ==== begin ====");
            Debug.Log("[SkinAudit] avatarRoot=" + root.name +
                " pos=" + Fmt(root.position) + " scale=" + Fmt(root.localScale));
            Debug.Log("[SkinAudit] visualRoot=" + (visualRoot == null ? "null" : visualRoot.name) +
                " localPos=" + (visualRoot == null ? "-" : Fmt(visualRoot.localPosition)) +
                " localScale=" + (visualRoot == null ? "-" : Fmt(visualRoot.localScale)) +
                " lossyScale=" + (visualRoot == null ? "-" : Fmt(visualRoot.lossyScale)));

            var cam = Camera.main;
            if (cam != null)
            {
                Debug.Log("[SkinAudit] camera=" + cam.name +
                    " pos=" + Fmt(cam.transform.position) +
                    " euler=" + Fmt(cam.transform.eulerAngles) +
                    " fov=" + cam.fieldOfView.ToString("F1", CultureInfo.InvariantCulture));
            }

            // 1. 骨骼真值：关键骨骼世界坐标 + 全体骨骼 Y 范围。
            var head = avatar.HeadBone;
            Debug.Log("[SkinAudit] headBone=" + (head == null ? "null" : head.name) +
                " worldY=" + (head == null ? "-" : F(head.position.y)));
            if (visualRoot != null)
            {
                float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                var all = visualRoot.GetComponentsInChildren<Transform>(false);
                for (int i = 0; i < all.Length; i++)
                {
                    var y = all[i].position.y;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                Debug.Log("[SkinAudit] boneTreeY=[" + F(minY) + "," + F(maxY) + "] count=" + all.Length);
                for (int k = 0; k < KeyBoneNames.Length; k++)
                {
                    var bone = FindByName(all, KeyBoneNames[k]);
                    if (bone != null)
                    {
                        Debug.Log("[SkinAudit] bone " + KeyBoneNames[k] +
                            " worldPos=" + Fmt(bone.position) +
                            " localScale=" + Fmt(bone.localScale));
                    }
                }
            }

            // 2/3. 逐渲染器对照。
            if (visualRoot == null)
            {
                Debug.Log("[SkinAudit] ==== end (no visualRoot) ====");
                return;
            }
            var renderers = visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            float bakedMinY = float.PositiveInfinity, bakedMaxY = float.NegativeInfinity;
            int bakedCount = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                var smr = renderers[i];
                var sb = new System.Text.StringBuilder(256);
                sb.Append("[SkinAudit] smr ").Append(smr.name);
                sb.Append(" enabled=").Append(smr.enabled);
                sb.Append(" node localPos=").Append(Fmt(smr.transform.localPosition));
                sb.Append(" localScale=").Append(Fmt(smr.transform.localScale));
                sb.Append(" sharedMesh.bounds=").Append(smr.sharedMesh == null ? "null" : FmtBounds(smr.sharedMesh.bounds));
                sb.Append(" localBounds=").Append(FmtBounds(smr.localBounds));
                sb.Append(" renderer.bounds=").Append(FmtBounds(smr.bounds));
                sb.Append(" rootBone=").Append(smr.rootBone == null ? "null" : smr.rootBone.name + "@" + F(smr.rootBone.position.y));
                sb.Append(" bones=").Append(smr.bones == null ? 0 : smr.bones.Length);
                sb.Append(" containsHead=").Append(head != null && smr.bones != null && System.Array.IndexOf(smr.bones, head) >= 0);
                // 非零 BlendShape 权重（经验性排除 morph 位移）。
                if (smr.sharedMesh != null)
                {
                    int bsCount = smr.sharedMesh.blendShapeCount;
                    var nonZero = new List<string>();
                    for (int b = 0; b < bsCount; b++)
                    {
                        var w = smr.GetBlendShapeWeight(b);
                        if (Mathf.Abs(w) > 0.01f)
                        {
                            nonZero.Add(smr.sharedMesh.GetBlendShapeName(b) + "=" + w.ToString("F0", CultureInfo.InvariantCulture));
                        }
                    }
                    if (nonZero.Count > 0)
                    {
                        sb.Append(" blendShapes[").Append(string.Join(",", nonZero.ToArray())).Append("]");
                    }
                }
                Debug.Log(sb.ToString());

                // BakeMesh：当前蒙皮输出的真实顶点（渲染器局部空间）。
                if (smr.sharedMesh != null && smr.enabled)
                {
                    var baked = new Mesh();
                    try
                    {
                        smr.BakeMesh(baked);
                        var lb = baked.bounds;
                        var m = smr.transform.localToWorldMatrix;
                        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                        for (int cx = 0; cx < 2; cx++)
                        for (int cy = 0; cy < 2; cy++)
                        for (int cz = 0; cz < 2; cz++)
                        {
                            var corner = new Vector3(
                                cx == 0 ? lb.min.x : lb.max.x,
                                cy == 0 ? lb.min.y : lb.max.y,
                                cz == 0 ? lb.min.z : lb.max.z);
                            var wy = m.MultiplyPoint3x4(corner).y;
                            if (wy < lo) lo = wy;
                            if (wy > hi) hi = wy;
                        }
                        Debug.Log("[SkinAudit] baked " + smr.name +
                            " localBounds=" + FmtBounds(lb) +
                            " worldY=[" + F(lo) + "," + F(hi) + "]");
                        if (lo < bakedMinY) bakedMinY = lo;
                        if (hi > bakedMaxY) bakedMaxY = hi;
                        bakedCount++;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning("[SkinAudit] BakeMesh failed for " + smr.name + ": " + e.Message);
                    }
                    finally
                    {
                        Object.Destroy(baked);
                    }
                }
            }
            if (bakedCount > 0)
            {
                Debug.Log("[SkinAudit] bakedWorldY total=[" + F(bakedMinY) + "," + F(bakedMaxY) +
                    "] height=" + F(bakedMaxY - bakedMinY) + " renderers=" + bakedCount);
            }
            Debug.Log("[SkinAudit] ==== end ====");
        }

        private static Transform FindByName(Transform[] all, string name)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == name || all[i].name.StartsWith(name + " ", System.StringComparison.Ordinal) ||
                    all[i].name.StartsWith(name + "(", System.StringComparison.Ordinal))
                {
                    return all[i];
                }
            }
            return null;
        }

        private static string F(float v)
        {
            return v.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
        }

        private static string FmtBounds(Bounds b)
        {
            return "c" + Fmt(b.center) + " e" + Fmt(b.extents);
        }
    }
}
