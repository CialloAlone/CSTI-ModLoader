using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [DRAG] 拖拽路径**只读**进出标记（B-1）。
    ///   · 只挂"进/出"标记，**不读取也不修改任何参数/返回值**（零行为影响）；
    ///   · 进出写入 `Diag` 的**环形缓冲**（保留最后 50 条），**正常推进不刷屏**；
    ///   · 仅在 `[HB]` 主线程停顿事件（>500ms 不推进）时把缓冲**整段打印**出来
    ///     ⇒ 最后一个"有 → 没有 ←"的方法名就是卡点。
    /// 方法名是本项目实测抓到的元数据里**真实存在**的（不猜、不扫全部方法）。
    /// </summary>
    public static class DragProbe
    {
        private static HarmonyLib.Harmony _h;
        public static int Installed, Failed;

        /// <summary>候选：类型名 → 方法名（B-1 圈定的 5 个 + 备选）。</summary>
        private static readonly string[] TypeNames = { "InGameCardBase", "InGameDraggableCard", "CardInteractionTrigger" };
        private static bool Hit(string n) => n.Contains("Drag") || n.Contains("Interaction") || n.Contains("Trigger");

        private static readonly (string Type, string Method)[] Candidates =
        {
            ("InGameCardBase", "OnBeginDrag"),
            ("InGameCardBase", "OnDrag"),
            ("InGameDraggableCard", "OnDragBegin"),
            ("InGameDraggableCard", "OnDragEnd"),
            ("InGameCardBase", "EndCardDrag"),
            ("InGameCardBase", "get_CardInteractions"),
            ("CardInteractionTrigger", "Trigger"),
        };

        public static void Install()
        {
            try
            {
                // 用户要求"架构纯净"：诊断探针默认关闭（cfg 写 Diag_DragProbe = true 才挂）
                var raw = "";
                try { raw = (MiniLoader.PrefFileRaw("Diag_DragProbe") ?? "").ToLower(); } catch { }
                if (!raw.Contains("true"))
                { MelonLogger.Msg("[DRAG] 诊断探针默认关闭（Diag_DragProbe 未开）→ 不挂任何 Hook"); return; }

                if (_h != null) return;
                _h = new HarmonyLib.Harmony("CSTI_MiniLoader.DragProbe");
                var pre = new HarmonyMethod(AccessTools.Method(typeof(DragProbe), nameof(Pre)));
                var post = new HarmonyMethod(AccessTools.Method(typeof(DragProbe), nameof(Post)));

                // ★ 兜底思路（Lead 裁定）：**不写死方法名** —— 在类型上枚举全部方法（含基类链），
                //   按方法名含 Drag/Interaction/Trigger 关键字挑选（只在诊断里挑，不涉及 mod/卡名 ✓）。
                //   名字对不上是今天反复踩的坑（托管名 ≠ 元数据名）→ 枚举能绕开它 ✓。
                foreach (var tn in TypeNames)
                {
                    var t = Diag.FindTypeByName(tn);
                    if (t == null)
                    {
                        Failed++;
                        MelonLogger.Warning("[DRAG] 查找 " + tn + " 失败: 类型不存在（托管名可能带命名空间）");
                        continue;
                    }

                    var found = 0;
                    var chain = new System.Collections.Generic.List<Type>();
                    for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType) chain.Add(cur);
                    foreach (var ct in chain)
                    foreach (var mi in ct.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (mi.IsAbstract || mi.ContainsGenericParameters) continue;
                        if (!Hit(mi.Name)) continue;
                        try
                        {
                            MelonLogger.Msg("[DRAG] 备选方法: " + ct.FullName + "." + mi.Name + "("
                                            + string.Join(",", Array.ConvertAll(mi.GetParameters(), p => p.ParameterType.Name)) + ")");
                            _h.Patch(mi, pre, post);
                            Installed++;
                            found++;
                        }
                        catch (Exception pe)
                        {
                            Failed++;
                            MelonLogger.Warning("[DRAG] 挂载失败 " + ct.Name + "." + mi.Name + ": "
                                                + pe.GetType().Name + " " + pe.Message);
                        }
                    }

                    if (found == 0)
                    {
                        Failed++;
                        MelonLogger.Warning("[DRAG] 查找 " + tn + " 失败: 类型找到了但**没有名字含 Drag/Interaction/Trigger 的方法**（枚举已含基类链）");
                    }
                }

                MelonLogger.Msg("[DRAG] 安装汇总: 成功=" + Installed + " 失败=" + Failed + "（只读，不改行为）");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[DRAG] 安装异常: " + e.GetType().Name + " " + e.Message);
            }
        }

        // ★ 只读前缀/后缀：只用 Harmony 注入的 __originalMethod / __instance，**不接触业务参数** ✓
        // ★★ [递归护栏] 探针自己读字段会再次触发被 hook 的 getter → 自递归（假死循环 ✗）。
        //    ThreadStatic 标记：重入时**直接返回**（不读字段、不抓栈、不记缓冲 ✓）。
        [ThreadStatic] private static bool _inProbe;

        public static void Pre(MethodBase __originalMethod, object __instance)
        {
            if (_inProbe) return;   // ★ 递归护栏：探针副本（自己读字段触发的重入）→ 立即返回，什么都不做
            _inProbe = true;
            try
            {
                var m = __originalMethod?.Name ?? "?";
                if (m == "IsValidTrigger") Diag.NoteValidTrigger();   // ★ 工作量计数（算不完 vs 死锁）
                var ctx = "";
                if (m.StartsWith("get_TriggerCards") || m.StartsWith("get_TriggerTags"))
                {
                    // 调用者点名（第一个非 CardInteractionTrigger/Harmony/本程序集的帧）
                    var st = new System.Diagnostics.StackTrace(1, false);
                    for (var fi = 0; fi < st.FrameCount; fi++)
                    {
                        var md = st.GetFrame(fi)?.GetMethod();
                        if (md == null) continue;
                        var dt = md.DeclaringType;
                        var dn = dt?.Name ?? "?";
                        if (dn.Contains("CardInteractionTrigger") || dn.Contains("Harmony") || dn.Contains("CSTI_MiniLoader")) continue;
                        Diag.NoteDragCaller((dt?.FullName ?? dn) + "." + md.Name);
                        break;
                    }

                    Diag.NoteDragFingerprint(__instance);
                }

                Diag.DragMark("→", __originalMethod?.DeclaringType?.Name + "." + m + ctx);
            }
            catch { }
            finally { _inProbe = false; }
        }

        public static void Post(MethodBase __originalMethod, object __instance)
        {
            try
            {
                var nm = __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name;
                Diag.DragMark("←", nm);
                // 落卡/退栈相关：顺手打一次"交互条数 + 拖拽栈读数"
                if (__originalMethod != null && (__originalMethod.Name == "EndCardDrag" ||
                                                 __originalMethod.Name == "OnDragEnd"))
                {
                    Diag.DragState(__originalMethod.Name + " 后", __instance);
                    Diag.EndDragCost(__originalMethod.Name + " 结束", -1, -1);   // ★ 本轮工作量汇总 + 复位
                }
            }
            catch { }
        }
    }
}
