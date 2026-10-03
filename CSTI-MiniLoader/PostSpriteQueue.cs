using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [POSTSPRITE] 延迟落盘队列 —— 照 PC `PostSpriteLoad.cs:41/65` + `WarpperFunction.cs:44`
    /// （`obj.PostSetEnQueue(setter, data)`）的语义：
    ///   · **入队点不变**（仍在 warp 时），只把"Sprite/AudioClip 类引用立刻写"改成"入队 + 稍后 flush"；
    ///   · flush 由我们**已有的 Tick 泵**驱动，**只执行队列**（逐条写回），**不做任何逻辑判断、不做时机猜测**；
    ///   · **默认关**（`cfg PostSpriteLoadQueue=true` 才启用），便于 A/B；全 try/catch、失败逐条告警。
    /// </summary>
    public static class PostSpriteQueue
    {
        public static int Enqueued, Flushed, FailedFlush;
        private static readonly List<(WeakReference Host, string Field, WeakReference Value)> Q = new();
        private static bool _loggedOff;

        /// <summary>是否启用（默认关）。cfg `PostSpriteLoadQueue=true` 打开。</summary>
        public static bool Enabled()
        {
            try
            {
                var raw = (MiniLoader.PrefFileRaw("PostSpriteLoadQueue") ?? "").ToLower();
                if (raw.Length > 0) return raw.Contains("true");
                var fp = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                if (File.Exists(fp))
                    foreach (var line in File.ReadAllLines(fp))
                    {
                        var s = line.Trim();
                        var eq = s.IndexOf("=", StringComparison.Ordinal);
                        if (eq > 0 && s.StartsWith("PostSpriteLoadQueue", StringComparison.OrdinalIgnoreCase))
                            return s.Substring(eq + 1).ToLower().Contains("true");
                    }
            }
            catch { }
            return false;   // 默认关
        }

        /// <summary>入队一条"引用落盘"（warp 时调用；若未启用则返回 false，调用方照旧立即写）。</summary>
        public static bool Enqueue(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase host, string fld,
            Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase value)
        {
            try
            {
                if (!Enabled())
                {
                    if (!_loggedOff) { _loggedOff = true; MelonLogger.Msg("[POSTSPRITE] 默认关闭（PostSpriteLoadQueue 未开）→ 引用仍立即写入"); }
                    return false;
                }

                if (host == null || value == null) return false;
                Q.Add((new WeakReference(host), fld, new WeakReference(value)));
                Enqueued++;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[POSTSPRITE] 入队失败 " + fld + ": " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>由 Tick 泵调用：**只执行队列**（逐条写回），不做逻辑判断。</summary>
        public static void Flush()
        {
            try
            {
                if (Q.Count == 0) return;
                var n = Q.Count;
                for (var i = 0; i < Q.Count; i++)
                {
                    var (h, fld, v) = Q[i];
                    try
                    {
                        if (!h.IsAlive || !v.IsAlive)
                        {
                            FailedFlush++;
                            MelonLogger.Warning("[POSTSPRITE] flush 失败(弱引用已死): 宿主存活=" + h.IsAlive
                                                + " 值存活=" + v.IsAlive + " 字段=" + fld);   // ★ 逐条打全原因
                            continue;
                        }

                        if (h.Target == null) { FailedFlush++; MelonLogger.Warning("[POSTSPRITE] flush 失败(宿主为 null): " + fld); continue; }
                        if (v.Target == null) { FailedFlush++; MelonLogger.Warning("[POSTSPRITE] flush 失败(值为 null): " + fld); continue; }

                        if (Diag.TryWriteMember(h.Target, fld, v.Target)) Flushed++;
                        else
                        {
                            FailedFlush++;
                            var mt = Diag.MemberTypeOf(h.Target, fld);
                            MelonLogger.Warning("[POSTSPRITE] flush 写回失败: " + h.Target.GetType().Name + "." + fld
                                                + " 成员类型=" + (mt == null ? "找不到成员" : mt.Name)
                                                + " 值类型=" + v.Target.GetType().Name);       // ★ 失败原因
                        }
                    }
                    catch (Exception e1)
                    {
                        FailedFlush++;
                        MelonLogger.Warning("[POSTSPRITE] flush 异常: " + e1.GetType().Name + " " + e1.Message);
                    }
                }

                Q.Clear();
                MelonLogger.Msg("[POSTSPRITE] 入队=" + Enqueued + " flush=" + Flushed + " 失败=" + FailedFlush
                                + "（本轮排出=" + n + "）");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[POSTSPRITE] flush 阶段异常: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
