using System;
using System.Collections.Generic;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [NAMESTYLE] **只读**命名风格与 CardImage 链排查（零行为改动）：
    ///  · SampleSpriteNames(40)：Sprite 桶名字抽样 40 条（判断命名风格：英文标识 / 内部 id / 中文）
    ///  · VanillaCardImageChain()：找若干**原版卡**（非 mod）打印 CardImage → sprite.name / texture.name
    ///    （学习"游戏里 CardImage 到底指向什么、名字从哪来"）
    /// </summary>
    public static class NameStyleProbe
    {
        public static void Run()
        {
            try
            {
                SampleSpriteNames(40);
                VanillaCardImageChain();
            }
            catch (Exception e) { MelonLogger.Warning("[NAMESTYLE] 失败: " + e.GetType().Name + " " + e.Message); }
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
                    var sn = "";
                    try { sn = Diag.Member(kv.Value, "name")?.ToString() ?? ""; } catch { }
                    var tn = "";
                    try { tn = Diag.Member(Diag.Member(kv.Value, "texture"), "name")?.ToString() ?? ""; } catch { }
                    var hasCn = false;
                    try { foreach (var ch in kv.Key ?? "") if (ch >= 0x4E00 && ch <= 0x9FFF) { hasCn = true; break; } } catch { }
                    lines.Add("[NAMESTYLE] 索引名=" + kv.Key + " sprite.name=" + sn + " texture.name=" + tn
                              + " 含中文=" + (hasCn ? "是" : "否"));
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
                    object ro = null;
                    try { ro = Diag.Retype(kv.Value) ?? kv.Value; } catch { }
                    if (ro == null) continue;
                    var isMod = false;
                    try { isMod = Diag.ModCardJsonSource.ContainsKey(kv.Key); } catch { }
                    if (isMod) continue;
                    considered++;
                    var img = Diag.Member(ro, "CardImage");
                    if (img == null) continue;
                    if (shown++ >= 6) break;
                    var sn = "";
                    try { sn = Diag.Member(img, "name")?.ToString() ?? ""; } catch { }
                    var tn = "";
                    try { tn = Diag.Member(Diag.Member(img, "texture"), "name")?.ToString() ?? ""; } catch { }
                    MelonLogger.Msg("[NAMESTYLE] 原版卡=" + (Diag.NameOf(ro) ?? kv.Key)
                                    + " CardImage=" + Diag.Cls(img) + " sprite.name=" + sn + " texture.name=" + tn);
                }
                MelonLogger.Msg("[NAMESTYLE] 原版卡(非 mod)=" + considered + " 其中有 CardImage 的已列 " + shown + " 条");
            }
            catch (Exception e) { MelonLogger.Warning("[NAMESTYLE] VanillaCardImageChain 失败: " + e.GetType().Name + " " + e.Message); }
        }
    }
}