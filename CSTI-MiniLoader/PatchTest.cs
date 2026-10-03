using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// Phase1 探针 + Phase2 通道（**全部默认关**；cfg 控制；只读枚举不挂任何钩子）。
    ///  · Diag_PatchTargets = true → 只枚举并打印真实方法签名（零 Hook）
    ///  · Diag_PatchTest    = true → 只挂 UniqueIDScriptable.ClearDict 一个（前缀只打点 + 只读判据）
    ///  · Diag_Phase2Append = true → 在 ClearDict 前缀里照 PC（ModLoader.cs:852）把 mod 对象 append 进 AllData，
    ///                              让游戏自己的循环替我们调 Init()（**我们不自己调、不轮询**）；60 秒后只读验证。
    /// </summary>
    public static class PatchTest
    {
        public static int Installed, Failed, Calls;
        public static int Appended, AppendedSkipped;
        private static HarmonyLib.Harmony _h;

        private const string TargetType = "UniqueIDScriptable";
        private const string TargetMethod = "ClearDict";

        private static readonly string[] EnumTypes =
        { "GameLoad", "GraphicsManager", "GuideManager", "LocalizationManager", "UniqueIDScriptable" };

        private static readonly (string Type, string Method)[] PcNames =
        {
            ("GameLoad", "AwakeWithLoadingScreen"),
            ("UniqueIDScriptable", "ClearDict"),
            ("GraphicsManager", "Init"),
            ("GuideManager", "Start"),
            ("LocalizationManager", "LoadLanguage"),
        };

        private static readonly string[] Keywords =
        { "Awake", "Start", "Init", "Load", "Setup", "BeginBefore", "Clear", "Refresh", "Compress", "PostSet" };

        /// <summary>读配置：先用 MiniLoader 的文件原文读取；早期它还没就绪时直接按约定路径读 cfg（只读、失败告警）。</summary>
        private static string Flag(string key)
        {
            try
            {
                var v = (MiniLoader.PrefFileRaw(key) ?? "").ToLower();
                if (v.Length > 0) return v;
            }
            catch { }

            try
            {
                var fp = Path.Combine(MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                if (File.Exists(fp))
                {
                    foreach (var line in File.ReadAllLines(fp))
                    {
                        var s2 = line.Trim();
                        var eq = s2.IndexOf("=", StringComparison.Ordinal);
                        if (eq > 0 && s2.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                            return s2.Substring(eq + 1).Trim().Trim((char)34).ToLower();
                    }
                }
                else
                {
                    MelonLogger.Warning("[PATCH-TEST] 早期读 cfg：文件不存在 " + fp);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PATCH-TEST] 早期读 cfg 失败: " + e.GetType().Name + " " + e.Message);
            }

            return "";
        }

        // ═══════════ ① 只读枚举 ═══════════
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
                        foreach (var c in FindTypesByName(tn)) MelonLogger.Warning("[PATCH-TARGETS]   候选类型: " + c.FullName);
                        continue;
                    }

                    var n = 0;
                    var lines = new List<string>();
                    for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                    {
                        MethodInfo[] ms;
                        try
                        {
                            ms = cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                        }
                        catch { continue; }

                        foreach (var m in ms)
                        {
                            if (m.IsAbstract || m.ContainsGenericParameters) continue;
                            if (!Keywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                            n++;
                            lines.Add("[PATCH-TARGETS] 候选: " + m.ReturnType.Name + " " + cur.Name + "." + m.Name
                                      + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                        }
                    }

                    MelonLogger.Msg("[PATCH-TARGETS] 类型=" + tn + " 方法=" + n + "（含基类链；关键字过滤；无 cap）");
                    foreach (var l in lines) MelonLogger.Msg(l);
                }

                MelonLogger.Msg("[PATCH-TARGETS] ===== PC 名字 vs 移动端 =====");
                foreach (var (tn, mn) in PcNames)
                {
                    var verdict = "?";
                    try
                    {
                        var t = Diag.FindTypeByName(tn);
                        if (t == null) verdict = "类型不存在";
                        else if (AccessTools.Method(t, mn) != null) verdict = "**同名方法存在**";
                        else
                        {
                            var alt = FindSimilar(t, mn);
                            verdict = "**方法名不同**" + (alt.Count == 0 ? "（未找到相似名）" : "（相似：" + string.Join(", ", alt) + "）");
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
                        if (t.Name.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) res.Add(t);
                }
            }
            catch { }
            return res;
        }

        private static List<string> FindSimilar(Type t, string pcName)
        {
            var res = new List<string>();
            try
            {
                for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                    foreach (var m in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (m.Name.IndexOf(pcName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pcName.IndexOf(m.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                            res.Add(cur.Name + "." + m.Name);
            }
            catch { }
            return res.Distinct().Take(12).ToList();
        }

        // ═══════════ ② Phase1 单钩子 ═══════════
        public static void Install()
        {
            try
            {
                if (!Flag("Diag_PatchTest").Contains("true"))
                {
                    MelonLogger.Msg("[PATCH-TEST] 默认关闭（Diag_PatchTest 未开）→ 零钩子");
                    return;
                }

                if (_h != null) return;   // 幂等 guard

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

        /// <summary>前缀体：全 try/catch、只打点 + 只读判据、立即返回（零行为影响）。</summary>
        public static void Pre()
        {
            try
            {
                Calls++;
                if (Calls <= 3)
                {
                    MelonLogger.Warning("[PATCH-TEST] " + TargetType + "." + TargetMethod + " 首次调用=是 次数=" + Calls);
                    try
                    {
                        var allData = GameLoad.Instance.DataBase.AllData;
                        var first = Diag.GetElem(allData, 0);
                        MelonLogger.Warning("[PATCH-TEST] ClearDict 时 AllData.Count=" + Diag.ElemCount(allData));
                        MelonLogger.Warning("[PATCH-TEST] AllData.Count=" + Diag.ElemCount(allData)
                                            + " 首元素=" + (first == null ? "null" : Diag.Cls(first)));
                    }
                    catch (Exception e2)
                    {
                        MelonLogger.Warning("[PATCH-TEST] AllData.Count 读取失败: " + e2.GetType().Name + " " + e2.Message);
                    }
                }

                if (Flag("Diag_Phase2Append").Contains("true")) Phase2Append();
            }
            catch { }
        }

        // ═══════════ ③ Phase2：append 进 AllData + 只读验证 ═══════════
        private static bool _appended;
        private static long _appendedAt;
        private static bool _verified;

        private static void Phase2Append()
        {
            try
            {
                if (_appended) return;
                _appended = true;
                _appendedAt = Environment.TickCount64;

                var allData = GameLoad.Instance.DataBase.AllData;
                var dict = MiniLoader.ItemDictionary(typeof(CardData));
                foreach (var kv in dict)
                {
                    try
                    {
                        var o = kv.Value;
                        if (o == null) { AppendedSkipped++; continue; }
                        if (Contains(allData, o)) { AppendedSkipped++; continue; }
                        allData.Add((UniqueIDScriptable)o);
                        Appended++;
                    }
                    catch (Exception e1)
                    {
                        AppendedSkipped++;
                        MelonLogger.Warning("[PHASE2] append 失败: " + e1.GetType().Name + " " + e1.Message);
                    }
                }

                MelonLogger.Warning("[PHASE2] 已 append 进 AllData: 成功=" + Appended + " 跳过=" + AppendedSkipped
                                    + " 之后 AllData.Count=" + Diag.ElemCount(allData));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PHASE2] append 阶段异常: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static bool Contains(object list, object item)
        {
            try
            {
                var n = (int)Diag.ElemCount(list);
                for (var i = 0; i < n; i++)
                    if (ReferenceEquals(Diag.GetElem(list, i), item)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>只读验证：游戏是否在 ClearDict 之后自己遍历 AllData 调了 Init()（判据：UniqueID 非空 / AllDrops 非空）。</summary>
        public static void Phase2Verify()
        {
            try
            {
                if (!_appended || _verified) return;
                if (Environment.TickCount64 - _appendedAt < 60000) return;
                _verified = true;

                var dict = MiniLoader.ItemDictionary(typeof(CardData));
                int n = 0, idOk = 0, dropOk = 0;
                foreach (var kv in dict)
                {
                    n++;
                    var o = Diag.Retype(kv.Value) ?? kv.Value;
                    var id = Diag.Member(o, "UniqueID")?.ToString();
                    if (!string.IsNullOrEmpty(id)) idOk++;
                    var dl = Diag.Member(o, "AllDrops");
                    if (dl != null && (int)Diag.ElemCount(dl) > 0) dropOk++;
                    if (n <= 3)
                        MelonLogger.Warning("[PHASE2] 验证样本: " + (Diag.NameOf(o) ?? kv.Key) + " UniqueID=" + (id ?? "null")
                                            + " AllDrops=" + (dl == null ? "null" : Diag.ElemCount(dl).ToString()));
                }

                MelonLogger.Warning("[PHASE2] 游戏自己 Init() 的验证: mod 卡=" + n + " UniqueID 非空=" + idOk
                                    + " AllDrops 非空=" + dropOk
                                    + (idOk > 0 ? "  ⇒ 游戏确实遍历并 Init 了" : "  ⇒ 未被 Init"));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PHASE2] 验证异常: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}