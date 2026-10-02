// ============================================================================
//  E2Probe —— 验证实验 E2（Lead 路线 C 的第一步）
// ----------------------------------------------------------------------------
//  目的：确认「UI 掉落预览」与「实际结算」是不是**同一个读函数**。
//  做法：给两个候选读点各挂一个**只打日志**的 Postfix，绝不改返回值、绝不写任何游戏对象。
//     · GameManager.GetCollectionDropsReport    （PLAN.md 推荐首选点）
//     · CardsDropCollection.FillDropList        （兜底点）
//  用户探索一次后看日志：
//     · 只有其中一个被调用 → 它就是唯一需要改的读点；
//     · 两个都被调用       → 说明预览与结算分别走各自的路径，两处都要处理。
//
//  注意：本工程默认 SkipHarmonyPatchAll=true（历史结论：给 LocalizationManager.LoadLanguage /
//  GuideManager.Start / GraphicsManager.Init 挂补丁会崩）。这里是**独立的最小补丁类**，
//  只挂上面两个方法，且整体包在 try/catch 里，方便快速回退（把 EnableE2Probe 置 false 即可）。
// ============================================================================

using System;
using HarmonyLib;
using MelonLoader;

namespace CSTI_MiniLoader
{
    public static class E2Probe
    {
        public static int GetReportCalls, FillDropListCalls;

        private static string InstName(object inst)
        {
            try
            {
                if (inst is UnityEngine.Object uo) return uo.GetType().Name + "(" + uo.name + ")";
                return inst?.GetType().Name ?? "<null>";
            }
            catch
            {
                return "<err>";
            }
        }

        [HarmonyPatch(typeof(GameManager), "GetCollectionDropsReport")]
        public static class PatchGetCollectionDropsReport
        {
            [HarmonyPostfix]
            public static void Post(object __instance, object[] __args)
            {
                try
                {
                    GetReportCalls++;
                    // 防刷屏：前 20 次全打，之后每 100 次打一条（万一 UI 每帧预览，日志会爆）
                    if (GetReportCalls <= 20 || GetReportCalls % 100 == 0)
                        MelonLogger.Msg("[E2] GameManager.GetCollectionDropsReport #" + GetReportCalls
                                        + " 实例=" + InstName(__instance)
                                        + " 参数数=" + (__args?.Length ?? -1));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[E2] post 异常: " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(CardsDropCollection), "FillDropList")]
        public static class PatchFillDropList
        {
            [HarmonyPostfix]
            public static void Post(object __instance, object[] __args)
            {
                try
                {
                    FillDropListCalls++;
                    if (FillDropListCalls <= 20 || FillDropListCalls % 100 == 0)
                        MelonLogger.Msg("[E2] CardsDropCollection.FillDropList #" + FillDropListCalls
                                        + " 实例=" + InstName(__instance)
                                        + " 参数数=" + (__args?.Length ?? -1));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[E2] post 异常: " + e.Message);
                }
            }
        }

        /// <summary>安装（由 MiniLoader.OnInitializeMelon 调用）。失败只记日志，不影响其它功能。</summary>
        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                harmony.PatchAll(typeof(PatchGetCollectionDropsReport));
                MelonLogger.Msg("[E2] 已挂 Postfix: GameManager.GetCollectionDropsReport（只打日志）");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[E2] 挂 GetCollectionDropsReport 失败: " + e.GetType().Name + " " + e.Message);
            }

            try
            {
                harmony.PatchAll(typeof(PatchFillDropList));
                MelonLogger.Msg("[E2] 已挂 Postfix: CardsDropCollection.FillDropList（只打日志）");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[E2] 挂 FillDropList 失败: " + e.GetType().Name + " " + e.Message);
            }

            DumpSignatures();
        }

        /// <summary>
        /// 打印候选读点的签名与 struct 字段表 —— 路线 C 要按这些签名写 Postfix（ref __result 重建）。
        /// 全部按**名字反射**查找，避免编译期硬依赖不存在的类型。
        /// </summary>
        public static void DumpSignatures()
        {
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public |
                                                      System.Reflection.BindingFlags.NonPublic |
                                                      System.Reflection.BindingFlags.Instance |
                                                      System.Reflection.BindingFlags.Static |
                                                      System.Reflection.BindingFlags.DeclaredOnly;
            try
            {
                foreach (var m in typeof(GameManager).GetMethods(BF))
                {
                    if (m.Name != "GetCollectionDropsReport") continue;
                    var ps = new System.Collections.Generic.List<string>();
                    foreach (var p in m.GetParameters()) ps.Add(p.ParameterType.Name + " " + p.Name);
                    MelonLogger.Msg("[E2SIG] GameManager." + m.Name + "(" + string.Join(", ", ps) + ") : "
                                    + m.ReturnType.Name + (m.IsStatic ? " [static]" : ""));
                }

                var asm = typeof(GameManager).Assembly;
                Type cardsDrop = null;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "CardsDropCollection") cardsDrop = t;
                }

                if (cardsDrop != null)
                    foreach (var m in cardsDrop.GetMethods(BF))
                    {
                        if (m.Name != "FillDropList") continue;
                        var ps = new System.Collections.Generic.List<string>();
                        foreach (var p in m.GetParameters()) ps.Add(p.ParameterType.Name + " " + p.Name);
                        MelonLogger.Msg("[E2SIG] CardsDropCollection." + m.Name + "(" + string.Join(", ", ps) + ") : "
                                        + m.ReturnType.Name + (m.IsStatic ? " [static]" : ""));
                    }

                foreach (var tn in new[]
                         {
                             "CollectionDropReport", "CardDrop", "CollectionDropInfo", "CardsDropCollection",
                             "EncounterResultEffect", "InGameEncounter", "GameManager"
                         })
                {
                    Type t = null;
                    foreach (var x in asm.GetTypes())
                    {
                        if (x.Name == tn) t = x;
                    }

                    if (t == null)
                    {
                        MelonLogger.Msg("[E2SIG] 类型 " + tn + " 未找到");
                        continue;
                    }

                    var fields = new System.Collections.Generic.List<string>();
                    foreach (var kv in WarpperClassGen.MainGen.GetOrGen(t))
                        fields.Add(kv.Key + ":" + (kv.Value.fldType?.Name ?? "?"));
                    MelonLogger.Msg("[E2SIG] 类型 " + tn + (t.IsValueType ? "(struct)" : "(class)")
                                    + " gen字段(" + fields.Count + "): " + string.Join(", ", fields));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[E2SIG] 失败: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
