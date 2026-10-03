using System;
using System.Collections.Generic;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [MISSIMG] **只读**缺图点名 + 原版素材名排查（零行为改动：不写游戏对象、不改索引、不加 Hook）。
    /// · Run()：逐张点名 CardImage 为空的 mod 卡（卡名/期望图名/索引命中桶/当前值/未解析行 + 三态定性，无 cap）
    /// · FuzzySearch(bucket, frag)：在某桶里模糊找名字（忽略大小写），标注来源（游戏注册表 / mod 图集）
    /// · VanillaCardImage(frag)：找名字含 frag 的**原版卡**，打印其 CardImage 运行时值与 sprite.name（权威名字）
    /// · CoverageReadout()：名字索引各桶项数 + 已登记 mod 资产数 + mod sprite 字典数
    /// 注：定性文案已按 Lead 更正 —— "索引里查不到"**不等于**"数据里没这张图"，
    ///     也可能是"我们查不到原版素材"（见 docs/loader/PC-LOADER-FLOW.md §14.4）。
    /// </summary>
    public static class MissingImgProbe
    {
        public static void Run()
        {
            try
            {
                var dict = MiniLoader.ItemDictionary(typeof(CardData));
                var modSprites = MiniLoader.ItemDictionary(typeof(UnityEngine.Sprite));

                int total = 0, missing = 0, hitGame = 0, hitMod = 0, noWhere = 0;
                var lines = new List<string>();

                foreach (var kv in dict)
                {
                    object ro = null;
                    try { ro = Diag.Retype(kv.Value) ?? kv.Value; } catch { }
                    if (ro == null) continue;
                    if (!Diag.ModCardJsonSource.ContainsKey(kv.Key)) continue;
                    total++;

                    var cur = Diag.Member(ro, "CardImage");
                    if (cur != null) continue;
                    missing++;

                    var name = Diag.NameOf(ro) ?? kv.Key;
                    var want = "(JSON 无 CardImageWarpData)";
                    try
                    {
                        if (Diag.ModCardJsonSource.TryGetValue(kv.Key, out var js) && js != null && js.IsObject &&
                            js.ContainsKey("CardImageWarpData"))
                            want = js["CardImageWarpData"].ToString();
                    }
                    catch { }

                    object gameHit = null, modHit = null;
                    try { gameHit = Diag.NameIndexFind("Sprite", want); } catch { }
                    try { if (modSprites != null && modSprites.ContainsKey(want)) modHit = modSprites[want]; } catch { }

                    string state;
                    if (modHit != null) { state = "② 只在 mod 图集里（查登记时机）"; hitMod++; }
                    else if (gameHit != null) { state = "① 游戏资产里有（查写入路径）"; hitGame++; }
                    else { state = "③ 索引里查不到（可能是原版素材未收录，需按原版同类卡的 CardImage 取名，未必是数据问题）"; noWhere++; }

                    var missLine = "无";
                    try
                    {
                        foreach (var m in Diag.ResolveMisses)
                            if (m != null && m.Contains(want)) { missLine = "有（见日志 [RESOLVE] 行）"; break; }
                    }
                    catch { }

                    lines.Add("[MISSIMG] 卡名=" + name + " 期望图名=" + want
                              + " 名字索引命中=" + (gameHit != null || modHit != null ? "是" : "否")
                              + " 命中桶=" + (modHit != null ? "mod图集" : gameHit != null ? "游戏注册表" : "无")
                              + " 当前值=" + (cur == null ? "null" : "Sprite")
                              + " | [RESOLVE] 未解析行=" + missLine
                              + " | 定性=" + state);
                }

                MelonLogger.Msg("[MISSIMG] mod 卡=" + total + " 缺图=" + missing
                                + "（① 游戏有=" + hitGame + " ② 仅 mod 图集=" + hitMod + " ③ 索引查不到=" + noWhere + "）");
                foreach (var l in lines) MelonLogger.Warning(l);

                FuzzySearch("Sprite", "meteor");
                VanillaCardImage("Meteor");
                CoverageReadout();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[MISSIMG] 失败: " + e.GetType().Name + " " + e.Message);
            }
        }

        // ═══════════ [MISSIMG-PROBE] 只读：原版素材名排查 ═══════════
        public static void FuzzySearch(string bucket, string frag)
        {
            try
            {
                var modSet = MiniLoader.ItemDictionary(typeof(UnityEngine.Sprite));
                var hits = new List<string>();
                var snap = Diag.NameIndexSnapshot(bucket);
                foreach (var kv in snap)
                {
                    if (kv.Key == null) continue;
                    if (kv.Key.IndexOf(frag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var isMod = false;
                    try { isMod = modSet != null && modSet.ContainsKey(kv.Key); } catch { }
                    var nm = "";
                    try { nm = Diag.Member(kv.Value, "name")?.ToString() ?? ""; } catch { }
                    hits.Add("[MISSIMG-PROBE] 命中桶=" + bucket + " 名字=" + kv.Key + " 来源=" + (isMod ? "mod图集" : "游戏注册表")
                             + " 类型=" + (kv.Value == null ? "null" : Diag.Cls(kv.Value)) + " sprite.name=" + nm);
                }
                MelonLogger.Msg("[MISSIMG-PROBE] 桶=" + bucket + " 总项=" + snap.Count + " 含\"" + frag + "\"项=" + hits.Count);
                foreach (var h in hits) MelonLogger.Warning(h);
                if (hits.Count == 0)
                    MelonLogger.Warning("[MISSIMG-PROBE] ⇒ 桶=" + bucket + " 里没有含\"" + frag + "\"的名字（索引覆盖不全或命名不同）");
            }
            catch (Exception e) { MelonLogger.Warning("[MISSIMG-PROBE] FuzzySearch 失败: " + e.GetType().Name + " " + e.Message); }
        }

        public static void VanillaCardImage(string frag)
        {
            try
            {
                var dict = MiniLoader.ItemDictionary(typeof(CardData));
                int n = 0, shown = 0;
                foreach (var kv in dict)
                {
                    object ro = null;
                    try { ro = Diag.Retype(kv.Value) ?? kv.Value; } catch { }
                    if (ro == null) continue;
                    var isMod = false;
                    try { isMod = Diag.ModCardJsonSource.ContainsKey(kv.Key); } catch { }
                    if (isMod) continue;
                    var nm = Diag.NameOf(ro) ?? "";
                    if (nm.IndexOf(frag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n++;
                    if (shown++ < 8)
                    {
                        var img = Diag.Member(ro, "CardImage");
                        var sn = "";
                        try { sn = img == null ? "" : (Diag.Member(img, "name")?.ToString() ?? ""); } catch { }
                        MelonLogger.Warning("[MISSIMG-PROBE] 原版卡=" + nm + " CardImage=" + (img == null ? "null" : Diag.Cls(img))
                                            + " sprite.name=" + (sn.Length == 0 ? "(空)" : sn));
                    }
                }
                MelonLogger.Msg("[MISSIMG-PROBE] 名字含\"" + frag + "\"的原版卡=" + n + "（最多列 8 条）");
            }
            catch (Exception e) { MelonLogger.Warning("[MISSIMG-PROBE] VanillaCardImage 失败: " + e.GetType().Name + " " + e.Message); }
        }

        public static void CoverageReadout()
        {
            try
            {
                var modSprites = MiniLoader.ItemDictionary(typeof(UnityEngine.Sprite));
                MelonLogger.Msg("[MISSIMG-PROBE] 名字索引各桶: " + string.Join(" ", Diag.NameIndexCounts())
                                + " | 已登记 mod 资产=" + Diag.NameIndexRegistered
                                + " | mod sprite 字典=" + (modSprites?.Count ?? 0));
            }
            catch (Exception e) { MelonLogger.Warning("[MISSIMG-PROBE] CoverageReadout 失败: " + e.GetType().Name + " " + e.Message); }
        }
    }
}
