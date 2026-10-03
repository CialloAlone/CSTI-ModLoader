using System;
using System.Collections.Generic;
using System.Linq;
using CSTI_MiniLoader.LoadUtil.DataFind;
using CSTI_MiniLoader.Patchers;
using CSTI_MiniLoader.WarpperClassGen;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CSTI_MiniLoader.LoadUtil;

public static class LoadResources
{
    public static T Pop<T>(this List<T> list)
    {
        if (list.Count == 0) return default;
        var result = list[^1];
        list.RemoveAt(list.Count - 1);
        return result;
    }

    public static void LoadEditorScriptableObject()
    {
        while (WaitForWarpperEditorNoGuidList.Count > 0)
        {
            var item = WaitForWarpperEditorNoGuidList.Pop();
            try
            {
                var json = item.CardData;
                if (json == null) continue;
                WarpFunc.JsonCommonWarpper(item.Obj, json);
                if (item.Obj is CardTabGroup && item.Obj.name.StartsWith("Tab_"))
                    WaitForAddCardTabGroup.Add(new ScriptableObjectPack(item.Obj, "", "", "", item.CardData));

                if (item.Obj is ContentPage)
                {
                    if (item.Obj.name.EndsWith("Default"))
                        WaitForAddDefaultContentPage.Add(new ScriptableObjectPack(item.Obj, "", "", "",
                            item.CardData));
                    else if (item.Obj.name.EndsWith("Main"))
                        WaitForAddMainContentPage.Add(new ScriptableObjectPack(item.Obj, "", "", "",
                            item.CardData));
                }

                if (item.Obj is GuideEntry entry) WaitForAddGuideEntry.Add(entry);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("LoadEditorScriptableObject " + ex.Message);
            }
        }
    }

    public static IEnumerable<Object> WithGameDataFinder(this IEnumerable<Object> enumerable)
    {
        foreach (var o in enumerable)
        {
            yield return o;
        }

        foreach (var data in GameLoad.Instance.DataBase.AllData)
        {
            yield return data;
            foreach (var o in data.Find())
            {
                yield return o;
            }
        }
    }

    /// <summary>
    /// 免 FindObjectsOfType 版本的资源注册。
    /// 原版用 Object.FindObjectsOfType(ScriptableObject)，但那条链路内部依赖
    /// UnityEngine.Resources::FindObjectsOfTypeAll —— 本机引擎里该 ICall 未注册，必然抛异常，
    /// 导致游戏资源注册表从未建立、mod 内容（卡牌/特质）永远不会出现。
    /// 改用游戏自己维护的 UniqueIDScriptable.AllUniqueObjects 静态字典（纯托管，无 ICall 依赖），
    /// 由 Pump 轮询驱动：字典一有内容就注册。
    /// </summary>
    public static bool LoadGameResourceFromRegistry()
    {
        try
        {
            var dict = UniqueIDScriptable.AllUniqueObjects;
            if (dict == null || dict.Count == 0) return false;

            int n = 0;
            foreach (var kv in dict)
            {
                var obj = kv.Value;
                if (obj == null) continue;
                RegObj(kv.Key, obj, obj.GetType());
                n++;
            }
            MelonLogger.Msg("[HOOKFREE] 资源注册表导入完成: " + n + " 个对象（原版 FindObjectsOfType 路径不可用）");
            return n > 0;
        }
        catch (Exception ex)
        {
            MelonLogger.Error("LoadGameResourceFromRegistry Error " + ex.Message);
            return false;
        }
    }

    public static void LoadGameResource()
    {
        // 优先走游戏自己的注册表（原版路径依赖被裁剪的 ICall，必然失败）
        if (LoadGameResourceFromRegistry()) return;

        try
        {
            foreach (var ele in Object.FindObjectsOfType(Il2CppType.Of<ScriptableObject>()).WithGameDataFinder())
            {
                if (ele is not UniqueIDScriptable)
                {
                    RegObj(ele.name, ele, ele.GetType());
                }
                else if (ele is UniqueIDScriptable idScriptable)
                {
                    RegObj(idScriptable.UniqueID, idScriptable, idScriptable.GetType());
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error("LoadGameResource Error " + ex.Message);
        }
    }


    public static void WarpperAllEditorMods()
    {
        // var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var keys = WaitForWarpperEditorGuidDict.Keys.ToList();
        // 计数必须放在 try 里面各自的位置上（上一版把 ok++ 写在 catch 里，得出「成功=0 失败=0」的假结论）
        int jsonNull = 0, warped = 0, exCount = 0;
        int perkSeen = 0, perkQueued = 0, perkNoGroupKey = 0;
        int perkGroupSeen = 0;
        var loggedTypes = new HashSet<string>();
        WarpFunc.ResetStats();
        // 事件卡诊断（见下方 [EVENTCARD]）：最多转储 4 张
        var eventCardDumped = 0;
        foreach (var key in keys)
        {
            try
            {
                var processingScriptableObjectPack = WaitForWarpperEditorGuidDict[key];
                WaitForWarpperEditorGuidDict.Remove(key);

                var json = processingScriptableObjectPack.CardData;
                if (json == null)
                {
                    jsonNull++;
                    if (jsonNull <= 3) MelonLogger.Warning("[WARP] json 为空: " + key);
                    continue;
                }

                warped++;
                WarpFunc.JsonCommonWarpper(processingScriptableObjectPack.Obj, json);

                // ★★ [WARP-JSONKEYS] 只读判据：**这个宿主实际拿到的 JSON 键清单**（去重每宿主一次、无 cap）。
                //    用来回答"`CardImageWarpData` 到底在不在我们手里的 JSON 里"——
                //    在 ⇒ 遍历/过滤把它丢了；不在 ⇒ 我们手里的 JSON 不是完整卡数据。
                //    同时列出"JSON 里有、gen 表里没有"的键（可能直接指向键名规范化那一步）。
                try
                {
                    var hostObj = processingScriptableObjectPack.Obj;
                    var gid = "-";
                    try { gid = Diag.Member(hostObj, "UniqueID")?.ToString() ?? "-"; } catch (Exception __e) { MelonLogger.Warning("[LoadResources] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                    var g8 = gid.Length > 8 ? gid.Substring(0, 8) : gid;
                    var hostKey = Diag.Cls(hostObj) + "/" + g8;
                    if (JsonKeysLogged.Add(hostKey))
                    {
                        var gen = WarpperClassGen.MainGen.GetOrGen(hostObj.GetType());
                        var missing = new List<string>();
                        foreach (var k in json.Keys)
                        {
                            var fld = k.EndsWith("WarpType") ? k.Substring(0, k.Length - 8) : k;
                            if (!gen.ContainsKey(fld)) missing.Add(k);
                        }

                        MelonLogger.Msg("[WARP-JSONKEYS] 宿主=" + hostKey + " 键数=" + json.Count
                                        + " 含CardImageWarpData=" + json.ContainsKey("CardImageWarpData")
                                        + " 含CardImage=" + json.ContainsKey("CardImage")
                                        + " 全部键=[" + string.Join(",", json.Keys) + "]");
                        MelonLogger.Msg("[WARP-JSONKEYS] 宿主=" + hostKey + " JSON有但gen表没有(" + missing.Count
                                        + ")=[" + string.Join(",", missing) + "]");
                    }
                }
                catch (Exception __e)
                {
                    MelonLogger.Warning("[WARP-JSONKEYS] 失败: " + __e.GetType().Name + " " + __e.Message);
                }

                // 每种类型只打一次：json 字段数 vs 生成器字段数（gen=0 说明 warp 会把所有键静默跳过）
                var tname = Diag.Cls(processingScriptableObjectPack.Obj);
                if (loggedTypes.Add(tname))
                {
                    var genFields = -1;
                    try { genFields = MainGen.GetOrGen(processingScriptableObjectPack.Obj.GetType()).Count; } catch (Exception __e) { MelonLogger.Warning("[LoadResources] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                    var sampleKeys = string.Join(",", json.Keys);
                    MelonLogger.Msg("[WARP] 首见类型 " + tname + " json字段=" + json.Count + " gen字段=" + genFields
                                    + " 例键=[" + sampleKeys + "] il2cpp类=" + tname);
                }

                if (tname == "PerkGroup" && perkGroupSeen++ < 2)
                    Diag.DumpJson("PerkGroup " + Diag.NameOf(processingScriptableObjectPack.Obj), json, 12);
                if (processingScriptableObjectPack.Obj is CardData cardData)
                {
                    if (cardData.CardType == CardTypes.Blueprint &&
                        json.ContainsKey("BlueprintCardDataCardTabGroup") &&
                        json["BlueprintCardDataCardTabGroup"].IsString && !string.IsNullOrWhiteSpace(
                            json["BlueprintCardDataCardTabGroup"].ToString()) &&
                        json.ContainsKey("BlueprintCardDataCardTabSubGroup") &&
                        json["BlueprintCardDataCardTabSubGroup"].IsString &&
                        !string.IsNullOrWhiteSpace(json["BlueprintCardDataCardTabSubGroup"].ToString()))
                        WaitForAddBlueprintCard.Add(new Tuple<string, string, CardData>(
                            json["BlueprintCardDataCardTabGroup"].ToString(),
                            json["BlueprintCardDataCardTabSubGroup"].ToString(), cardData));

                    if (json.ContainsKey("ItemCardDataCardTabGpGroup") &&
                        json["ItemCardDataCardTabGpGroup"].IsArray)
                        for (var i = 0; i < json["ItemCardDataCardTabGpGroup"].Count; i++)
                            if (json["ItemCardDataCardTabGpGroup"][i].IsString &&
                                ItemDictionary(typeof(CardTabGroup)).TryGetValue(
                                    json["ItemCardDataCardTabGpGroup"][i].ToString(),
                                    out var tabGroup))
                                (tabGroup as CardTabGroup)!.IncludedCards.Add(cardData);

                    if (json.ContainsKey("CardDataCardFilterGroup") && json["CardDataCardFilterGroup"].IsArray)
                        for (var i = 0; i < json["CardDataCardFilterGroup"].Count; i++)
                            if (json["CardDataCardFilterGroup"][i].IsString && !string.IsNullOrWhiteSpace(
                                    json["CardDataCardFilterGroup"][i].ToString()))
                                WaitForAddCardFilterGroupCard.Add(new Tuple<string, CardData>(
                                    json["CardDataCardFilterGroup"][i].ToString(), cardData));

                    cardData.FillDropsList();
                    // [诊断] 用户报「开局事件选选项没给东西」，存档里 EncounteredEvents 只有
                    // Windy_Event_Gift —— 说明那个事件是 mod 卡。这里把事件类卡片的 JSON 键与
                    // warp 后的关键字段打出来，看「选项/结果」数据到底有没有进来。
                    try
                    {
                        var nm = Diag.NameOf(cardData);
                        var isEventName = nm.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (eventCardDumped < 8 && (isEventName || eventCardDumped < 2))
                        {
                            eventCardDumped++;
                            Diag.EventCardDumps++;
                            // [lean] 只打一行汇总（条数 + 产物链是否完整）；[full] 打 JSON 全量 + 逐字段明细
                            if (!MiniLoader.DiagFull)
                            {
                                Diag.LogEventCardSummary(cardData, nm, json);
                            }
                            else
                            {
                                MelonLogger.Msg("[EVENTCARD] " + nm + " GUID=" + Diag.SafeUniqueId(cardData)
                                                + " json键数=" + json.Count + (isEventName ? "  <名字含Event>" : ""));
                                MelonLogger.Msg("[EVENTCARD]   CardInteractions=" + Diag.Render(cardData.CardInteractions)
                                                + " CardTags=" + Diag.Render(cardData.CardTags)
                                                + " AllDrops=" + Diag.Render(cardData.AllDrops));
                                Diag.DumpJson("EVENTCARD " + nm, json, 130);
                                // [C 判据] CardTags 是否被「按名字」解析成非空数组
                                Diag.DumpNamedArray(cardData, "CardTags", "EVENTCARD " + nm);
                                // [正向判据] 事件选项的产出链：DismantleActions[i].ActionName + ProducedCards[0]
                                Diag.DumpEffectArray(cardData, "DismantleActions",
                                    "EVENTCARD " + nm + " GUID=" + Diag.SafeUniqueId(cardData)?.Substring(0, 8));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("[EVENTCARD] 转储失败: " + e.GetType().Name + " " + e.Message);
                    }
                    // var FillDropsList = typeof(CardData).GetMethod("FillDropsList", bindingFlags);
                    // if (FillDropsList != null)
                    // {
                    //     FillDropsList.Invoke(item.Value.obj, null);
                    // }
                }
                else if (processingScriptableObjectPack.Obj is CharacterPerk perk)
                {
                    perkSeen++;
                    MiniLoader.ModPerks.Add(perk);   // 收集起来，稍后挂进 PerkTabGroup.ContainedPerks
                    if (perkSeen <= 2) Diag.DumpJson("CharacterPerk#" + perkSeen + " " + Diag.NameOf(perk), json, 45);
                    if (json.ContainsKey("CharacterPerkPerkGroup") && json["CharacterPerkPerkGroup"].IsString &&
                        !string.IsNullOrWhiteSpace(json["CharacterPerkPerkGroup"].ToString()))
                    {
                        var wantGroup = json["CharacterPerkPerkGroup"].ToString();
                        WaitForAddPerkGroup.Add(new Tuple<string, CharacterPerk>(wantGroup, perk));
                        perkQueued++;
                        if (perkQueued <= 8)
                            MelonLogger.Msg("[PERK] 待挂特质: 组=\"" + wantGroup + "\" 特质对象名=" + Diag.NameOf(perk)
                                            + " GUID=" + Diag.SafeUniqueId(perk));
                    }
                    else
                    {
                        perkNoGroupKey++;
                        if (perkNoGroupKey <= 5)
                            MelonLogger.Warning("[PERK] 特质 JSON 里没有 CharacterPerkPerkGroup: " + Diag.NameOf(perk)
                                                + " 键=[" + string.Join(",", json.Keys) + "]");
                    }
                }
                else if (processingScriptableObjectPack.Obj is GameStat stat)
                {
                    if (json.ContainsKey("VisibleGameStatStatListTab") &&
                        json["VisibleGameStatStatListTab"].IsString &&
                        !string.IsNullOrWhiteSpace(json["VisibleGameStatStatListTab"].ToString()))
                        WaitForAddVisibleGameStat.Add(new Tuple<string, GameStat>(
                            json["VisibleGameStatStatListTab"].ToString(), stat));
                }
                else if (processingScriptableObjectPack.Obj is PlayerCharacter character)
                {
                    foreach (var pair in ItemDictionary(typeof(Gamemode)))
                    {
                        var mode = pair.Value as Gamemode;
                        var il2CppReferenceArray = (Il2CppArrayBase<PlayerCharacter>)mode.PlayableCharacters;
                        Il2CppSystem.Array.Resize(ref il2CppReferenceArray, mode.PlayableCharacters.Length + 1);
                        mode.PlayableCharacters = (Il2CppReferenceArray<PlayerCharacter>)il2CppReferenceArray;
                        mode.PlayableCharacters[^1] = character;
                    }

                    WaitForAddJournalPlayerCharacter.Add(new ScriptableObjectPack(character, "", "", "",
                        processingScriptableObjectPack.CardData));
                }
            }
            catch (Exception ex)
            {
                exCount++;
                if (exCount <= 5)
                    MelonLogger.Warning("[WARP异常] key=" + key + " " + ex.GetType().Name + ": " + ex.Message
                                        + "\n" + (ex.StackTrace ?? "").Split('\n').FirstOrDefault());
                Debug.LogWarning("WarpperAllEditorMods " + ex.Message);
            }
        }

        MelonLogger.Msg("[WARP统计] 处理=" + keys.Count + " json空=" + jsonNull + " 已warp=" + warped
                        + " 异常=" + exCount + " | " + WarpFunc.StatLine());
        MelonLogger.Msg("[PERK统计] CharacterPerk=" + perkSeen + " 有组名=" + perkQueued
                        + " 缺组名键=" + perkNoGroupKey + " 待挂载队列=" + WaitForAddPerkGroup.Count);
        WarpFunc.DumpSamples();

        LoadPatchMain.OnceWarp = true;
    }

    public static void MatchAndWarpperAllEditorGameSrouce()
    {
        foreach (var item in AllGUIDDict.Values)
        {
            try
            {
                if (item is CardData cardData)
                {
                    foreach (var tag in cardData.CardTags)
                    {
                        if (!AllCardTagGuidCardDataDict.ContainsKey(tag.name))
                            AllCardTagGuidCardDataDict.Add(tag.name, new Dictionary<string, CardData>());

                        if (AllCardTagGuidCardDataDict.TryGetValue(tag.name, out var dict))
                            dict.Add(cardData.UniqueID, cardData);
                    }
                }
            }
            catch
            {
                //Debug.LogWarning("MatchAndWarpperAllEditorGameSrouce Match " + ex.Message);
            }
        }

        while (WaitForMatchAndWarpperEditorGameSourceList.Count > 0)
        {
            var item = WaitForMatchAndWarpperEditorGameSourceList.Pop();
            try
            {
                if (item.CardData == null)
                    continue;
                var json = item.CardData;

                if (json.ContainsKey("MatchTagWarpData") && json["MatchTagWarpData"].IsArray &&
                    json["MatchTagWarpData"].Count > 0)
                {
                    if (!AllCardTagGuidCardDataDict.TryGetValue(json["MatchTagWarpData"][0].ToString(),
                            out var dict))
                        continue;
                    var MatchList = dict.Keys.ToList();

                    for (var i = 1; i < json["MatchTagWarpData"].Count; i++)
                    {
                        if (AllCardTagGuidCardDataDict.TryGetValue(json["MatchTagWarpData"][i].ToString(),
                                out var next_dict))
                            MatchList = MatchList.Intersect(next_dict.Keys).ToList();
                    }

                    foreach (var match in MatchList)
                    {
                        if (AllGUIDDict.TryGetValue(match, out var card))
                        {
                            if (card is CardData cardData)
                            {
                                if (json.ContainsKey("MatchTypeWarpData") && json["MatchTypeWarpData"].IsString)
                                    if (cardData.CardType.ToString() !=
                                        json["MatchTypeWarpData"].ToString())
                                        continue;
                                WarpFunc.JsonCommonWarpper(card, json);
                                cardData.FillDropsList();
                                // var FillDropsList = typeof(CardData).GetMethod("FillDropsList", bindingFlags);
                                // if (FillDropsList != null)
                                //     FillDropsList.Invoke(card, null);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("MatchAndWarpperAllEditorGameSrouce Warpper " + ex.Message);
            }
        }
    }


    /// <summary>GSM 逐条判据的计数（本轮已处理条数 / 预期总条数，来自离线解包：63 条）。</summary>
    /// <summary>[WARP-JSONKEYS] 每宿主只打一次（去重，无 cap）。</summary>
    private static readonly HashSet<string> JsonKeysLogged = new();

    private static int GsmSeen;
    private const int GsmTotalExpected = 63;

    public static void WarpperAllEditorGameSrouces()
    {
            try { Diag.PhaseMarkOnce("[ANCHOR] WarpperAllEditorGameSrouces 入口（warp 内候选）"); } catch { }
            // ★★ [NAMEIDX 修复] 已实证锚点（真机 [ANCHOR] t=118968ms 确认真执行）⇒ 在此重放 mod 登记：
            //    重放后名字索引里就会有 mod 自建 sprite，warp 解析引用时即可命中（顺序无关登记）。
            try { Diag.ReplayTrigger = "WarpperAll 入口"; Diag.ReapplyNameRegistrations(null); } catch (Exception __e) { MelonLogger.Warning("[LoadResources] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
        // var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        //foreach (var item in WaitForWarpperEditorGameSourceGUIDList)
        while (WaitForWarpperEditorGameSourceGUIDList.Count > 0)
        {
            var item = WaitForWarpperEditorGameSourceGUIDList.Pop();
            try
            {
                if (item.Obj == null)
                {
                    // [2026-10-03] 与 PC 侧语义对齐：先 AllGUIDDict（mod 对象），再查**游戏注册表**
                    // —— Windy 的"精灵能力"就是靠 GSM 改造**游戏自带**卡牌（采摘/编织/缠线/粘土…）。
                    if (AllGUIDDict.TryGetValue(item.CardDirOrGuid, out var obj))
                    {
                        item.Obj = obj;
                    }
                    else
                    {
                        var reg = UniqueIDScriptable.AllUniqueObjects;
                        if (reg != null && reg.TryGetValue(item.CardDirOrGuid, out var gameObj) && gameObj != null)
                            item.Obj = gameObj;
                        else
                            continue;
                    }
                }

                // [2026-10-03] 目标若是游戏自带对象，包装类型常是基类 UniqueIDScriptable →
                // 字段表（gen）会拿不到 DismantleActions 等字段。这里统一先按真实类名重建代理，
                // 后面「采样 / warp / 字段存在性检查」都用具体类型。
                try
                {
                    if (Diag.Retype(item.Obj) is ScriptableObject retypedObj) item.Obj = retypedObj;
                }
                catch (Exception __e) { MelonLogger.Warning("[LoadResources] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

                var processingScriptableObjectPack = item;

                if (item.CardData != null)
                {
                    var json = item.CardData;
                    if (json.ContainsKey("MatchTagWarpData") && json["MatchTagWarpData"].IsArray &&
                        json["MatchTagWarpData"].Count > 0)
                    {
                        WaitForMatchAndWarpperEditorGameSourceList.Add(item);
                        continue;
                    }

                    if (json.ContainsKey("ModLoaderSpecialOverwrite") && json["ModLoaderSpecialOverwrite"].IsBoolean &&
                        (bool)json["ModLoaderSpecialOverwrite"])
                    {
                        JsonUtility.FromJsonOverwrite(item.CardData.ToJson(), item.Obj);
                    }

                    // [GSM 判据] 改造前/后逐字段对比：`[GSM] 柠檬草(guid): Actions 3→5 ✓ / DroppedCards 2→4 ✓`
                    // 这是"精灵能力到底改没改到"的唯一客观证据（Lead 指定：日志里的数字才算判据）。
                    // [GSM 判据] 改造前/后**内容级**对比（条数 + 元素名字，例如"让风精灵采摘柠檬草"）；
                    // Lead 指定：内容/条数/值任一变化即算成功；未变化时额外 dump 消费端收到的 warp 对。
                    List<string> gsmFieldNames = null;
                    Dictionary<string, string> gsmBefore = null;
                    if (MiniLoader.DiagLean && !string.IsNullOrEmpty(item.CardDirOrGuid))
                    {
                        try
                        {
                            var pairs = Diag.GsmFieldsOf(json);
                            gsmFieldNames = new List<string>();
                            foreach (var (fld, _, _, _) in pairs) gsmFieldNames.Add(fld);
                            if (gsmFieldNames.Count > 0)
                            {
                                gsmBefore = Diag.FieldSnapshot(item.Obj, gsmFieldNames);
                                GsmSeen++;
                            }
                            else
                            {
                                gsmFieldNames = null;
                            }
                        }
                        catch
                        {
                            gsmFieldNames = null;
                        }
                    }

                    WarpFunc.JsonCommonWarpper(item.Obj, json);

                    if (gsmFieldNames != null)
                    {
                        try { Diag.LogGsmEntry(GsmSeen, GsmTotalExpected, item.Obj, item.CardDirOrGuid, json, gsmBefore); }
                        catch (Exception __e) { MelonLogger.Warning("[LoadResources] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                    }
                }

                if (item.Obj is CardData cardData)
                {
                    cardData.FillDropsList();
                    // var FillDropsList = typeof(CardData).GetMethod("FillDropsList", bindingFlags);
                    // if (FillDropsList != null)
                    // {
                    //     FillDropsList.Invoke(item.Obj, null);
                    // }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("WarpperAllEditorGameSrouces " + ex.Message);
            }
        }
    }
}
