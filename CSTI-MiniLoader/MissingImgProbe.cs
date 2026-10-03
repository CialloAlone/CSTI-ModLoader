using System;
using System.Collections.Generic;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// [MISSIMG] **只读**缺图点名：对每张 `CardImage` 为空的 mod 卡，逐张给出
    ///   卡名 / 期望图名（JSON `CardImageWarpData`）/ 名字索引命中（游戏桶 or mod 图集桶）/ 当前值 / 是否有 [RESOLVE] 未解析行。
    /// 三态定性：
    ///   ① 名字在**游戏资产**里有 ⇒ 为什么没写入（查写入路径）
    ///   ② 名字只在 **mod 图集**里有 ⇒ 查登记时机
    ///   ③ 两边都没有 ⇒ 数据里就没这张图（正常）
    /// **零行为改动**（不写任何游戏对象、不改索引、不加 Hook）。
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
                    if (!Diag.ModCardJsonSource.ContainsKey(kv.Key)) continue;   // 只看 mod 卡
                    total++;

                    var cur = Diag.Member(ro, "CardImage");
                    if (cur != null) continue;                                   // 有图的不点名
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

                    // 名字索引命中（只读；既有的 NameIndexFind 含精确→忽略大小写→归一化）
                    object gameHit = null, modHit = null;
                    try { gameHit = Diag.NameIndexFind("Sprite", want); } catch { }
                    try
                    {
                        if (modSprites != null && modSprites.ContainsKey(want)) modHit = modSprites[want];
                    }
                    catch { }

                    string state;
                    if (modHit != null) { state = "② 只在 mod 图集里（查登记时机）"; hitMod++; }
                    else if (gameHit != null) { state = "① 游戏资产里有（查写入路径）"; hitGame++; }
                    else { state = "③ 两边都没有（数据里就没这张图，正常）"; noWhere++; }

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
                                + "（① 游戏有=" + hitGame + " ② 仅 mod 图集=" + hitMod + " ③ 两边都没有=" + noWhere + "）");
                foreach (var l in lines) MelonLogger.Warning(l);   // 逐张、无 cap
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[MISSIMG] 失败: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
