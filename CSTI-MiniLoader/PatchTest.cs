using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [PATCH-TARGETS] 只读"方法枚举"（默认关；**不挂任何东西也能打**）。
    /// 目的：移动端 il2cpp 的方法名与 PC 版常不一致（例如 `GameLoad.AwakeWithLoadingScreen` 在移动端找不到），
    /// 先枚举真实签名，再决定 Phase 1 挂谁 —— 即"**先枚举、后挂载**"。
    /// 开关：
    ///   · cfg `Diag_PatchTargets = true` → **只枚举并打印**（零 Hook）
    ///   · cfg `Diag_PatchTest    = true` → 才尝试挂 **唯一一个** 目标（Phase 1 用；前缀体只打点）
    /// 两者都不设 = 零动作（架构纯净）。
    /// </summary>
    public static class PatchTest
    {
        public static int Installed, Failed, Calls;
        private static HarmonyLib.Harmony _h;

        /// <summary>Phase 1 逐轮切换的唯一目标（第 1 轮 = GameLoad.AwakeWithLoadingScreen）。</summary>
        private const string TargetType = "UniqueIDScriptable";
        private const string TargetMethod = "ClearDict";

        /// <summary>Phase 1 后续几轮要用的类型（一并枚举，便于点名）。</summary>
        private static readonly string[] EnumTypes =
        {
            "GameLoad", "GraphicsManager", "GuideManager", "LocalizationManager", "UniqueIDScriptable"
        };

        /// <summary>PC 侧真实名字（用于"移动端是否同名"的对照）。</summary>
        private static readonly (string Type, string Method)[] PcNames =
        {
            ("GameLoad", "AwakeWithLoadingScreen"),
            ("UniqueIDScriptable", "ClearDict"),
            ("GraphicsManager", "Init"),
            ("GuideManager", "Start"),
            ("LocalizationManager", "LoadLanguage"),
        };

        private static readonly string[] Keywords =
        {
            "Awake", "Start", "Init", "Load", "Setup", "BeginBefore", "Clear", "Refresh", "Compress", "PostSet"
        };

        private static string Flag(string key)
        {
            try { return (MiniLoader.PrefFileRaw(key) ?? "").ToLower(); } catch { return ""; }
        }

        // ═══════════ ① 只读枚举（Diag_PatchTargets=true 时执行；零 Hook） ═══════════
        public static void Enumerate()
        {
            try
            {
                if (!Flag("Diag_PatchTargets").Contains("true"))
                {
                    MelonLogger.Msg("[PATCH-TARGETS] 默认关闭（Diag_PatchTargets 未开）→ 不枚举");
                    return;
                }

                foreach (var tn in EnumTypes)
                {
                    var t = Diag.FindTypeByName(tn);
                    if (t == null)
                    {
                        MelonLogger.Warning("[PATCH-TARGETS] 类型=" + tn + " → **类型不存在（可能带命名空间）**；候选：");
                        foreach (var cand in FindTypesByName(tn))
                            MelonLogger.Warning("[PATCH-TARGETS]   候选类型: " + cand.FullName);
                        continue;
                    }

                    var chain = new List<Type>();
                    for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType) chain.Add(cur);

                    var n = 0;
                    var lines = new List<string>();
                    foreach (var ct in chain)
                    {
                        MethodInfo[] ms;
                        try
                        {
                            ms = ct.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                        }
                        catch { continue; }

                        foreach (var m in ms)
                        {
                            if (m.IsAbstract || m.ContainsGenericParameters) continue;
                            if (!Keywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                            n++;
                            lines.Add("[PATCH-TARGETS] 候选: " + m.ReturnType.Name + " " + ct.Name + "." + m.Name
                                      + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                        }
                    }

                    MelonLogger.Msg("[PATCH-TARGETS] 类型=" + tn + " 方法=" + n + "（含基类链；关键字过滤；无 cap）");
                    foreach (var l in lines) MelonLogger.Msg(l);
                }

                // ② PC/移动端 名字对照
                MelonLogger.Msg("[PATCH-TARGETS] ===== PC 名字 vs 移动端 =====");
                foreach (var (tn, mn) in PcNames)
                {
                    string verdict;
                    try
                    {
                        var t = Diag.FindTypeByName(tn);
                        if (t == null) verdict = "类型不存在";
                        else if (AccessTools.Method(t, mn) != null) verdict = "**同名方法存在 ✓**";
                        else
                        {
                            var alt = FindSimilar(t, mn);
                            verdict = "**方法名不同 ✗**" + (alt.Count == 0 ? "（未找到相似名）" : "（相似：" + string.Join(", ", alt) + "）");
                        }
                    }
                    catch (Exception e) { verdict = "检查异常 " + e.GetType().Name; }

                    MelonLogger.Msg("[PATCH-TARGETS] PC=" + tn + "." + mn + " → 移动端 " + verdict);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PATCH-TARGETS] 枚举失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>按名字片段在全部已加载程序集里找类型（用于"类型不存在"时给候选）。</summary>
        private static List<Type> FindTypesByName(string frag)
        {
            var res = new List<Type>();
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = a.GetTypes(); } catch { continue; }
                    foreach (var t in ts)
                        if (t.Name.Equals(frag, StringComparison.OrdinalIgnoreCase) ||
                            t.Name.Contains(frag, StringComparison.OrdinalIgnoreCase))
                            res.Add(t);
                }
            }
            catch { }
            return res;
        }

        /// <summary>在类型里找与 PC 名字"相似"的方法（去掉前后缀后包含关系）。</summary>
        private static List<string> FindSimilar(Type t, string pcName)
        {
            var res = new List<string>();
            try
            {
                for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                {
                    foreach (var m in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.Equals(pcName, StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains(pcName, StringComparison.OrdinalIgnoreCase) ||
                            pcName.Contains(m.Name, StringComparison.OrdinalIgnoreCase))
                            res.Add(cur.Name + "." + m.Name);
                    }
                }
            }
            catch { }
            return res.Distinct().Take(12).ToList();
        }

        // ═══════════ ③ Phase 1 单钩子（Diag_PatchTest=true 才挂；只打点） ═══════════
        public static void Install()
        {
            try
            {
                if (!Flag("Diag_PatchTest").Contains("true"))
                {
                    MelonLogger.Msg("[PATCH-TEST] 默认关闭（Diag_PatchTest 未开）→ 零钩子");
                    return;
                if (_h != null) return;   // 已挂过 → 不重复挂（幂等 ✓）
                }

                var t = Diag.FindTypeByName(TargetType);
                if (t == null)
                {
                    Failed++;
                    MelonLogger.Warning("[PATCH-TEST] 挂载失败：找不到类型 " + TargetType + "（跑 Diag_PatchTargets=true 看真实签名）");
                    return;
                }

                var mi = AccessTools.Method(t, TargetMethod);
                if (mi == null)
                {
                    Failed++;
                    MelonLogger.Warning("[PATCH-TEST] 挂载失败：类型 " + TargetType + " 里找不到方法 " + TargetMethod
                                        + "（跑 Diag_PatchTargets=true 看真实签名）");
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
                    // ★ 只读判据：确认"游戏在这之后自己遍历 AllData 调 Init()"这个前提在移动端是否成立（PC 的 IL 里就是这样）
                    try
                    {
                        var allData = GameLoad.Instance.DataBase.AllData;
                        MelonLogger.Warning("[PATCH-TEST] ClearDict 时 AllData.Count=" + Diag.ElemCount(allData));
                        var first = Diag.GetElem(allData, 0);
                        MelonLogger.Warning("[PATCH-TEST] AllData.Count=" + Diag.ElemCount(allData)
                                            + " 首元素=" + (first == null ? "null" : Diag.Cls(first)));
                    }
                    catch (Exception e2)
                    {
                        MelonLogger.Warning("[PATCH-TEST] AllData.Count 读取失败: " + e2.GetType().Name + " " + e2.Message);
                    }
            }
            catch { }
        }
    }
}
