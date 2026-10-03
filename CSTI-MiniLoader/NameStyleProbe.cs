using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [NAMESTYLE] **只读**命名风格与 CardImage 链排查。
    /// ★★ 默认关闭（cfg NameStyleProbe=true 才跑）★★
    ///   事故记录（2026-10-03）：首版默认开启后进程在 [MISSIMG] 之后死亡（pid 为空、日志 1.3 MB 止于 16:26:05），
    ///   头号嫌疑=本探针对 null/非 Sprite 对象读 .texture/.name。现全部加护栏：null 跳、逐项 try/catch。
    /// </summary>
    public static class NameStyleProbe
    {
        private static bool _loggedOff;

        public static bool Enabled()
        {
            try
            {
                var raw = (MiniLoader.PrefFileRaw("NameStyleProbe") ?? "").ToLower();
                if (raw.Length > 0) return raw.Contains("true");
                var fp = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                if (File.Exists(fp))
                    foreach (var line in File.ReadAllLines(fp))
                    {
                        var s = line.Trim();
                        var eq = s.IndexOf("=", StringComparison.Ordinal);
                        if (eq > 0 && s.StartsWith("NameStyleProbe", StringComparison.OrdinalIgnoreCase))
                            return s.Substring(eq + 1).ToLower().Contains("true");
                    }
            }
            catch { }
            return false;
        }

        public static void Run()
        {
            try
            {
                if (!Enabled())
                {
                    if (!_loggedOff)
                    {
                        _loggedOff = true;
                        MelonLogger.Msg("[NAMESTYLE] 默认关闭（NameStyleProbe 未开）→ 不跑（首版默认开启曾致进程死亡，现已加护栏）");
                    }
                    return;
                }

                SampleSpriteNames(40);
                VanillaCardImageChain();
            }
            catch (Exception e) { MelonLogger.Warning("[NAMESTYLE] 失败: " + e.GetType().Name + " " + e.Message); }
        }

        private static string SafeName(object o)
        {
            try { if (o == null) return ""; return Diag.Member(o, "name")?.ToString() ?? ""; }
            catch { return ""; }
        }

        public static void SampleSpriteNames(int max)
        {
            try
            {
                var snap = Diag.NameIndexSnapshot("Sprite");
                var lines = new List<string>();
                int n = 0;
                foreach (var kv in snap)
                {
                    if (n++ >= max) break;
                    try
                    {
                        if (kv.Value == null) continue;
                        var sn = SafeName(kv.Value);
                        string tn = "";
                        try
                        {
                            var tex = Diag.Member(kv.Value, "texture");
                            if (tex != null) tn = SafeName(tex);
                        }
                        catch { }

                        var hasCn = false;
                        try
                        {
                            var key = kv.Key ?? "";
                            foreach (var ch in key) if (ch >= 0x4E00 && ch <= 0x9FFF) { hasCn = true; break; }
                        }
                        catch { }

                        lines.Add("[NAMESTYLE] 索引名=" + kv.Key + " sprite.name=" + sn + " texture.name=" + tn
                                  + " 含中文=" + (hasCn ? "是" : "否"));
                    }
                    catch (Exception e1)
                    {
                        MelonLogger.Warning("[NAMESTYLE] 抽样项异常(已跳过): " + e1.GetType().Name + " " + e1.Message);
                    }
                }
                MelonLogger.Msg("[NAMESTYLE] Sprite 桶共 " + snap.Count + " 项，抽样 " + lines.Count + " 条");
                foreach (var l in lines) MelonLogger.Msg(l);
            }
            catch (Exception e) { MelonLogger.Warning("[NAMESTYLE] SampleSpriteNames 失败: " + e.GetType().Name + " " + e.Message); }
        }

        public static void VanillaCardImageChain()
        {
            try
            {
                var dict = MiniLoader.ItemDictionary(typeof(CardData));
                int shown = 0, considered = 0;
                foreach (var kv in dict)
                {
                    try
                    {
                        object ro = Diag.Retype(kv.Value) ?? kv.Value;
                        if (ro == null) continue;
                        var isMod = false;
                        try { isMod = Diag.ModCardJsonSource.ContainsKey(kv.Key); } catch { }
                        if (isMod) continue;
                        considered++;

                        var img = Diag.Member(ro, "CardImage");
                        if (img == null) continue;
                        if (shown++ >= 6) break;

                        var sn = SafeName(img);
                        string tn = "";
                        try
                        {
                            var tex = Diag.Member(img, "texture");
                            if (tex != null) tn = SafeName(tex);
                        }
                        catch { }

                        MelonLogger.Msg("[NAMESTYLE] 原版卡=" + (Diag.NameOf(ro) ?? kv.Key)
                                        + " CardImage=" + Diag.Cls(img) + " sprite.name=" + sn + " texture.name=" + tn);
                    }
                    catch (Exception e1)
                    {
                        MelonLogger.Warning("[NAMESTYLE] 原版卡项异常(已跳过): " + e1.GetType().Name + " " + e1.Message);
                    }
                }
                MelonLogger.Msg("[NAMESTYLE] 原版卡(非 mod)=" + considered + " 其中有 CardImage 的已列 " + shown + " 条");
            }
            catch (Exception e) { MelonLogger.Warning("[NAMESTYLE] VanillaCardImageChain 失败: " + e.GetType().Name + " " + e.Message); }
        }
    }
}