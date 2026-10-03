using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [PATCH-TEST] Phase 1 单钩子存活实验（**默认关闭** ✓、一次只挂一个 ✓、只打点不改行为 ✓）。
    /// 背景：HookFree.cs:11-20 记载"给 ① LocalizationManager.LoadLanguage ② GuideManager.Start
    /// ③ GraphicsManager.Init 挂 Harmony 会在启动/读档崩溃（补丁体是否执行无关）"——但**当年没做分层二分**，
    /// 分不清是"挂载行为"还是"调用时机"。本实验按"从最外层起"的顺序逐个复现：
    ///   Round 1 = GameLoad.AwakeWithLoadingScreen（最外层，只打点）
    /// 开关：cfg `Diag_PatchTest = true` 才挂；**不设 = 零钩子**（架构纯净 ✓）。
    /// 崩了就把这一行开关关掉即可（无需改代码）。
    /// </summary>
    public static class PatchTest
    {
        public static int Installed, Failed, Calls;
        private static HarmonyLib.Harmony _h;

        /// <summary>Round 1 目标（最外层）。</summary>
        private const string TargetType = "GameLoad";
        private const string TargetMethod = "AwakeWithLoadingScreen";

        public static void Install()
        {
            try
            {
                var raw = "";
                try { raw = (MiniLoader.PrefFileRaw("Diag_PatchTest") ?? "").ToLower(); } catch { }
                if (!raw.Contains("true"))
                {
                    MelonLogger.Msg("[PATCH-TEST] 默认关闭（Diag_PatchTest 未开）→ 零钩子");
                    return;
                }

                var t = Diag.FindTypeByName(TargetType);
                if (t == null)
                {
                    Failed++;
                    MelonLogger.Warning("[PATCH-TEST] 挂载失败：找不到类型 " + TargetType);
                    return;
                }

                var mi = AccessTools.Method(t, TargetMethod);
                if (mi == null)
                {
                    Failed++;
                    MelonLogger.Warning("[PATCH-TEST] 挂载失败：类型 " + TargetType + " 里找不到方法 " + TargetMethod);
                    return;
                }

                _h = new HarmonyLib.Harmony("CSTI_MiniLoader.PatchTest");
                _h.Patch(mi, new HarmonyMethod(AccessTools.Method(typeof(PatchTest), nameof(Pre))));
                Installed++;
                MelonLogger.Warning("[PATCH-TEST] 已挂 " + TargetType + "." + TargetMethod + "（只打点，不改行为）");
            }
            catch (Exception e)
            {
                Failed++;
                MelonLogger.Warning("[PATCH-TEST] 挂载异常：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>前缀体：全 try/catch、只打一行、立即返回（零行为影响）。</summary>
        public static void Pre()
        {
            try
            {
                Calls++;
                if (Calls <= 3)
                    MelonLogger.Warning("[PATCH-TEST] " + TargetType + "." + TargetMethod + " 首次调用=是 次数=" + Calls);
            }
            catch { }
        }
    }
}