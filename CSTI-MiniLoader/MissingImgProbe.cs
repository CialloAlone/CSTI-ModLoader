using System;
using System.Collections.Generic;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [MISSIMG] **只读**缺图点名 + 原版素材名排查（零行为改动：不写游戏对象、不改索引、不加 Hook）。
    /// · Run()：逐张点名 CardImage 为空的 mod 卡（三态定性，无 cap）+ 触发下面几个只读探针
    /// · FuzzySearch(bucket, frag)：桶内模糊找名字（忽略大小写），标注来源（游戏注册表 / mod 图集）
    /// · VanillaCardImage(frag)：名字含 frag 的**原版卡** → 其 CardImage 运行时值 + sprite.name
    /// · VanillaCardByName(frag)：同上但支持中文片段（本地化名），列最多 10 条
    /// · ChineseNameSample(max)：Sprite 桶里"名字含中文"的项抽样（验证移动端素材名是否中文）
    /// · CoverageReadout()：名字索引各桶项数 + 已登记 mod 资产 + mod sprite 字典数
    /// 注：③ 的定性按 Lead 更正为"索引里查不到（可能是原版素材未收录/命名不同），未必是数据问题"。
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
                    else { state = "③ 索引里查不到（可能是原版素材未收录/命名不同，未必是数据问题）"; noWhere++; }

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
                VanillaCardByName("陨石");
                VanillaCardByName("流星");
                VanillaCardByName("Meteor");
                ChineseNameSample(30);
                CoverageReadout();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[MISSIMG] 失败: " + e.GetType().Name + " " + e.Message);
            }
        }

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

        /// <summary>[只读] 按名字片段（支持中文）找原版卡 → CardImage + sprite.name（移动端真实素材名）。</summary>
        public static void VanillaCardByName(string frag)
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
                    if (shown++ < 10)
                    {
                        var img = Diag.Member(ro, "CardImage");
                        var sn = "";
                        try { sn = img == null ? "" : (Diag.Member(img, "name")?.ToString() ?? ""); } catch { }
                        MelonLogger.Warning("[MISSIMG-PROBE] 原版卡(名含\"" + frag + "\")=" + nm
                                            + " CardImage=" + (img == null ? "null" : Diag.Cls(img))
                                            + " sprite.name=" + (sn.Length == 0 ? "(空)" : sn));
                    }
                }
                MelonLogger.Msg("[MISSIMG-PROBE] 名字含\"" + frag + "\"的原版卡=" + n + "（最多列 10 条）");
            }
            catch (Exception e) { MelonLogger.Warning("[MISSIMG-PROBE] VanillaCardByName 失败: " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>[只读] Sprite 桶里"名字含中文"的项抽样（验证"移动端素材名是否中文"）。</summary>
        public static void ChineseNameSample(int max)
        {
            try
            {
                var snap = Diag.NameIndexSnapshot("Sprite");
                int cn = 0, shown = 0;
                var outLines = new List<string>();
                foreach (var kv in snap)
                {
                    var hasCn = false;
                    try
                    {
                        var s = kv.Key ?? "";
                        foreach (var ch in s) if (ch >= 0x4E00 && ch <= 0x9FFF) { hasCn = true; break; }
                    }
                    catch { }
                    if (!hasCn) continue;
                    cn++;
                    if (shown++ < max)
                    {
                        var sn = "";
                        try { sn = Diag.Member(kv.Value, "name")?.ToString() ?? ""; } catch { }
                        outLines.Add("[MISSIMG-PROBE] 中文名项=" + kv.Key + " sprite.name=" + sn
                                     + " 类型=" + (kv.Value == null ? "null" : Diag.Cls(kv.Value)));
                    }
                }
                MelonLogger.Msg("[MISSIMG-PROBE] Sprite 桶总项=" + snap.Count + " 含中文项=" + cn + "（抽样 " + outLines.Count + " 条）");
                foreach (var l in outLines) MelonLogger.Warning(l);
            }
            catch (Exception e) { MelonLogger.Warning("[MISSIMG-PROBE] ChineseNameSample 失败: " + e.GetType().Name + " " + e.Message); }
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