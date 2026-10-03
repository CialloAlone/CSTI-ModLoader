using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [DROPSFIX] 照 PC 同点位重跑 `CardData.FillDropsList()`（`DoWarpperLoader.cs:82/146/210` 那三处对应点）。
    /// 真机证据：mod 卡 174 张里 **100 张 `AllDrops` 为空**（UniqueID 都非空 ⇒ 对象在、掉落表没填）。
    /// 纪律：纯托管（反射/Traverse，零裸内存）✓、全 try/catch ✓、失败逐条告警 ✓、
    ///       **默认开启（这是修复不是诊断）** ✓，但保留开关可关（回归用）✓、不造循环（只在两个既有时机点各跑一次）✓。
    /// </summary>
    public static class DropsFix
    {
        public static int Tried, Filled, FailedCall;
        private static bool _loggedOff;
        private static readonly HashSet<string> PhasesDone = new();

        private static bool Enabled()
        {
            try
            {
                var raw = (MiniLoader.PrefFileRaw("DropsFix") ?? "").ToLower();
                if (raw.Length > 0) return !raw.Contains("false");
                var fp = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                if (System.IO.File.Exists(fp))
                    foreach (var line in System.IO.File.ReadAllLines(fp))
                    {
                        var s = line.Trim();
                        if (s.StartsWith("DropsFix", StringComparison.OrdinalIgnoreCase) && s.IndexOf("=", StringComparison.Ordinal) > 0)
                            return !s.ToLower().Contains("false");
                    }
            }
            catch { }
            return true;   // 默认开启
        }

        /// <summary>在指定时机点重跑一次（幂等：同一 phase 只跑一次）。</summary>
        public static void RunAll(string phase)
        {
            try
            {
                if (!Enabled())
                {
                    if (!_loggedOff) { _loggedOff = true; MelonLogger.Msg("[DROPSFIX] 已关闭（cfg DropsFix=false）→ 不重跑"); }
                    return;
                }

                if (!PhasesDone.Add(phase)) return;

                var dict = MiniLoader.ItemDictionary(typeof(CardData));
                int n = 0, beforeEmpty = 0;
                foreach (var kv in dict)
                {
                    n++;
                    object o = null;
                    try { o = Diag.Retype(kv.Value) ?? kv.Value; } catch { }
                    if (o == null) continue;

                    var dl = Diag.Member(o, "AllDrops");
                    if (dl != null && (int)Diag.ElemCount(dl) == 0) beforeEmpty++;

                    try
                    {
                        var mi = o.GetType().GetMethod("FillDropsList",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mi == null) { FailedCall++; continue; }
                        mi.Invoke(o, null);
                        Tried++;
                        var after = Diag.Member(o, "AllDrops");
                        if (after != null && (int)Diag.ElemCount(after) > 0) Filled++;
                    }
                    catch (Exception e1)
                    {
                        FailedCall++;
                        MelonLogger.Warning("[DROPSFIX] 重跑失败 " + (Diag.NameOf(o) ?? kv.Key) + ": "
                                            + e1.GetType().Name + " " + e1.Message);
                    }
                }

                MelonLogger.Warning("[DROPSFIX] 时机=" + phase + " 卡=" + n + " 重跑前 AllDrops 空=" + beforeEmpty
                                    + " 已调用=" + Tried + " 之后非空=" + Filled + " 调用失败=" + FailedCall);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[DROPSFIX] 时机=" + phase + " 阶段异常: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}