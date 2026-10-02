using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using CSTI_MiniLoader.LoadUtil;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;
using UnityEngine;
using Exception = System.Exception;
using Object = UnityEngine.Object;

namespace CSTI_MiniLoader.Patchers;

[SuppressMessage("ReSharper", "EmptyGeneralCatchClause")]
public static class LoadPatchMain
{
    // ===== 诊断开关：逐个关掉补丁以定位“装上去就崩”的元凶 =====
    public static bool SkipLocalizationPostfix = true;
    public static bool SkipGuideStartPrefix = true;
    public static bool SkipGraphicsInitPostfix = false;   // 本轮先关它

    [HarmonyPostfix, HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.LoadLanguage))]
    public static void LocalizationManagerLoadLanguagePostfix()
    {
            if (SkipLocalizationPostfix) return;   // [DIAG] 本轮跳过该补丁
        try
        {
            LoadLocalizationPublic();
        }
        catch (Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
    }

    public static void LoadLocalizationPublic()
    {
        var regex = new Regex(@"\\n");
        if (LocalizationManager.Instance.Languages[LocalizationManager.CurrentLanguage].LanguageName == "简体中文")
            foreach (var pair in WaitForLoadCSVList)
                try
                {
                    if (pair.Item1.Contains("SimpCn"))
                    {
                        var currentTexts = LocalizationManager.CurrentTexts;
                        var dictionary = CSVParser.LoadFromString(pair.Item2);
                        foreach (var keyValuePair in dictionary)
                            if (!currentTexts.ContainsKey(keyValuePair.Key) && keyValuePair.Value.Count >= 2)
                            {
                                var chLocal = regex.Replace(keyValuePair.Value[1], "\n");
                                if (!string.IsNullOrWhiteSpace(chLocal.Trim()))
                                    currentTexts.Add(keyValuePair.Key, chLocal);
                            }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("LoadLocalization " + ex.Message);
                }

        if (LocalizationManager.Instance.Languages[LocalizationManager.CurrentLanguage].LanguageName == "English")
            foreach (var pair in WaitForLoadCSVList)
                try
                {
                    if (pair.Item1.Contains("SimpEn"))
                    {
                        var currentTexts = LocalizationManager.CurrentTexts;
                        var dictionary = CSVParser.LoadFromString(pair.Item2);
                        foreach (var keyValuePair in dictionary)
                            if (!currentTexts.ContainsKey(keyValuePair.Key) && keyValuePair.Value.Count >= 2)
                            {
                                var enLocal = regex.Replace(keyValuePair.Value[0], "\n");
                                if (!string.IsNullOrWhiteSpace(enLocal.Trim()))
                                    currentTexts.Add(keyValuePair.Key, enLocal);
                            }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("LoadLocalization " + ex.Message);
                }
    }

    public static void Deconstruct<TKey, TVal>(this KeyValuePair<TKey, TVal> pair, out TKey key, out TVal val)
    {
        key = pair.Key;
        val = pair.Value;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GuideManager), nameof(GuideManager.Start))]
    public static void GuideManagerStartPrefix(GuideManager __instance)
    {
            if (SkipGuideStartPrefix) return;   // [DIAG] 本轮跳过该补丁
        try
        {
            LoadGuideEntryPublic(__instance);

            AddPlayerCharacterPublic(__instance);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
    }

    public static void AddPlayerCharacterPublic(GuideManager instance)
    {
        try
        {
            MelonCoroutines.Start(WaiterForContentDisplayer());
        }
        catch (Exception ex)
        {
            Debug.LogWarning("AddPlayerCharacter" + ex.Message);
        }
    }


    public static bool OnceWarp;

    private static IEnumerator WaiterForContentDisplayer()
    {
        var done = false;
        while (true)
        {
            List<Object> objs;
            try
            {
                objs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ContentDisplayer>()).ToList();
            }
            catch (Exception e)
            {
                try
                {
                    objs = [Resources.Load("Assets/JournalTourist")];
                    if (objs[0] == null)
                    {
                        objs = [];
                    }
                }
                catch (Exception exception)
                {
                    MelonLogger.Error(exception);
                    objs = [];
                }
            }


            foreach (var o in objs)
            {
                var obj = (ContentDisplayer)o;
                if (obj.gameObject.name != "JournalTourist") continue;
                ContentDisplayer? displayer = null;
                GameObject? clone = null;
                try
                {
                    clone = Object.Instantiate(obj.gameObject);
                    displayer = clone.GetComponent<ContentDisplayer>();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("FXMask Warning " + ex.Message);
                }

                if (displayer == null)
                    break;
                if (clone != null)
                {
                    clone.name = "JournalDefaultSample";
                    clone.hideFlags = HideFlags.HideAndDontSave;
                    CustomGameObjectListDict.Add(clone.name, clone);
                    CustomContentDisplayerDict.Add(clone.name, displayer);
                    done = true;
                }

                break;
            }

            if (done) break;

            yield return new WaitForSeconds(0.5f);
        }

        var displayers = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ContentDisplayer>());
        foreach (var displayer in displayers)
            try
            {
                if (!CustomContentDisplayerDict.ContainsKey(displayer.name) &&
                    displayer is ContentDisplayer contentDisplayer)
                    CustomContentDisplayerDict.Add(displayer.name, contentDisplayer);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("CustomContentDisplayerDict Warning " + ex.Message);
            }

        while (!OnceWarp) yield return null;

        while (WaitForAddDefaultContentPage.Count > 0)
        {
            var item = WaitForAddDefaultContentPage.Pop();
            try
            {
                if (item.Obj != null && CustomGameObjectListDict.ContainsKey(item.Obj.name))
                    continue;
                if (CustomGameObjectListDict.TryGetValue("JournalDefaultSample", out var sample))
                {
                    GameObject? clone = null;
                    ContentDisplayer? displayer = null;
                    try
                    {
                        clone = Object.Instantiate(sample);
                        displayer = clone.GetComponent(Il2CppType.Of<ContentDisplayer>()) as ContentDisplayer;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("FXMask Warning " + ex.Message);
                    }

                    if (displayer == null) continue;

                    var modPage = item.Obj as ContentPage;
                    if (modPage == null) continue;

                    var tDisplayer = Traverse.Create(displayer);
                    var pages = tDisplayer.Field<List<ContentPage>>("ExplicitPageContent").Value;
                    pages.Clear();
                    pages.Add(modPage);
                    tDisplayer.Field<ContentPage>("DefaultPage").Value = modPage;

                    if (item.Obj != null)
                    {
                        var nameParts = item.Obj.name.Split('_');
                        if (nameParts.Length > 2 && clone != null)
                        {
                            clone.name = nameParts[0] + "_" + nameParts[1];
                            clone.hideFlags = HideFlags.HideAndDontSave;
                            CustomGameObjectListDict.Add(clone.name, clone);
                            CustomContentDisplayerDict.Add(clone.name, displayer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("WaiterForContentDisplayer WaitForAddDefaultContentPage " + ex.Message);
            }
        }

        while (WaitForAddMainContentPage.Count > 0)
        {
            var item = WaitForAddMainContentPage.Pop();
            try
            {
                if (item.Obj != null)
                {
                    var nameParts = item.Obj.name.Split('_');
                    if (nameParts.Length > 2 && CustomContentDisplayerDict.TryGetValue(
                            nameParts[0] + "_" + nameParts[1],
                            out var displayer))
                    {
                        var pages = displayer.ExplicitPageContent;
                        pages?.Add((ContentPage)item.Obj);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("WaiterForContentDisplayer WaitForAddMainContentPage " + ex.Message);
            }
        }

        while (WaitForAddJournalPlayerCharacter.Count > 0)
        {
            var item = WaitForAddJournalPlayerCharacter.Pop();
            try
            {
                if (item.Obj is not PlayerCharacter character)
                    continue;

                var json = item.CardData;
                if (json != null && json.ContainsKey("PlayerCharacterJournalName") &&
                    json["PlayerCharacterJournalName"].IsString &&
                    !string.IsNullOrWhiteSpace(json["PlayerCharacterJournalName"].ToString()))
                    if (CustomContentDisplayerDict.TryGetValue(json["PlayerCharacterJournalName"].ToString(),
                            out var displayer))
                        character.Journal = displayer;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("WaiterForContentDisplayer PlayerCharacterJournalName " + ex.Message);
            }
        }
    }

    public static void LoadGuideEntryPublic(GuideManager instance)
    {
        try
        {
            foreach (var entry in WaitForAddGuideEntry) instance.AllEntries.Add(entry);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("LoadGuideEntry" + ex.Message);
        }
    }

    // [HarmonyPrefix, HarmonyPatch(typeof(UniqueIDScriptable), nameof(UniqueIDScriptable.ClearDict))]
    public static void LoadAndInit()
    {
        AllItemDictionary[typeof(Sprite)] = new Dictionary<string, object>();
        AllItemDictionary[typeof(AudioClip)] = new Dictionary<string, object>();
        try
        {
            var swAll = System.Diagnostics.Stopwatch.StartNew();
            var swStep = System.Diagnostics.Stopwatch.StartNew();
            MelonLogger.Msg("[STEP] 0 探针：纹理/精灵 API");
            LoadArchMod.ProbeTextureApis();
            MelonLogger.Msg("[STEP] 1 LoadGameResource");
            LoadResources.LoadGameResource();
            if (MiniLoader.DiagFull) try { Diag.ProbeEnumerationApis(); }
            catch (Exception pe) { MelonLogger.Warning("[PROBE] 探针失败: " + pe.GetType().Name + " " + pe.Message); }
            if (MiniLoader.DiagFull) try { Diag.ProbeMissingIcalls(); }
            catch (Exception ie) { MelonLogger.Warning("[ICALLPROBE] 探针失败: " + ie.GetType().Name + " " + ie.Message); }
            if (MiniLoader.DiagFull) try { Diag.TestSpritePipeline(); }
            catch (Exception se2) { MelonLogger.Warning("[SPRITETEST] 探针失败: " + se2.GetType().Name + " " + se2.Message); }
            if (MiniLoader.DiagFull) try { Diag.ProbeAudioIcalls(); }
            catch (Exception ae) { MelonLogger.Warning("[AUDIOPROBE] 探针失败: " + ae.GetType().Name + " " + ae.Message); }
            try
            {
                if (MiniLoader.DiagFull) Diag.DumpGenFields(typeof(PerkGroup));
                if (MiniLoader.DiagFull) Diag.DumpGenFields(typeof(PerkTabGroup));
                if (MiniLoader.DiagFull) Diag.DumpGenFields(typeof(CharacterPerk));
            }
            catch (Exception ge) { MelonLogger.Warning("[GEN] 探针失败: " + ge.GetType().Name + " " + ge.Message); }
            StepDone("0~1 探针 + LoadGameResource", swStep);
            var allDataBefore = Diag.AllDataCount();
            MelonLogger.Msg("[ALLDATA] 加载 mod 前 DataBase.AllData 条目数 = " + allDataBefore
                            + " | AddToGameDataBase=" + !MiniLoader.SkipGameDataBaseAdd);
            MelonLogger.Msg("[STEP] 2 LoadAllArchMod");
            Dictionary<string, (long Size, long Content, string Detail)> invSnap = null;
            try { invSnap = Diag.SnapshotGameContainers(20); }
            catch (Exception ie2) { MelonLogger.Warning("[INVARIANT] 采样失败: " + ie2.Message); }
            LoadArchMod.LoadAllArchMod();
            MelonLogger.Msg("[STEP] 3 LoadEditorScriptableObject");
            LoadResources.LoadEditorScriptableObject();
            StepDone("2~3 arch 解析 + 编辑器对象", swStep);
            MelonLogger.Msg("[STEP] 4 WarpperAllEditorMods");
        try
        {
            MelonLogger.Msg("[队列尺寸] 待warp(Guid)=" + MiniLoader.WaitForWarpperEditorGuidDict.Count
                            + "  待warp(NoGuid)=" + MiniLoader.WaitForWarpperEditorNoGuidList.Count
                            + "  AllGUIDDict=" + MiniLoader.AllGUIDDict.Count
                            + "  AllScriptableObjectDict=" + MiniLoader.AllScriptableObjectDict.Count
                            + "  CustomGameObject=" + MiniLoader.CustomGameObjectListDict.Count);
        }
        catch (Exception e) { MelonLogger.Warning("[队列尺寸] 失败: " + e.Message); }
            LoadResources.WarpperAllEditorMods();
            StepDone("4 warp 全部 mod JSON（含名字索引首建）", swStep);
            try { Diag.CompareGameContainers(invSnap, "warp 后"); }
            catch (Exception ie3) { MelonLogger.Warning("[INVARIANT] 对比失败: " + ie3.Message); }
            if (MiniLoader.DiagFull) try { Diag.DumpModPerkGroups(); Diag.DumpModPerksPostWarp(); }
            catch (Exception pge) { MelonLogger.Warning("[PG] 探针失败: " + pge.GetType().Name + " " + pge.Message); }
            MelonLogger.Msg("[STEP] 5 WarpperAllEditorGameSrouces");
            LoadResources.WarpperAllEditorGameSrouces();
            MelonLogger.Msg("[STEP] 6 MatchAndWarpperAllEditorGameSrouce");
            LoadResources.MatchAndWarpperAllEditorGameSrouce();
            if (MiniLoader.DiagFull) try { Diag.DumpPerkTabGroups(4); }
            catch (Exception te) { MelonLogger.Warning("[PTG] 探针失败: " + te.GetType().Name + " " + te.Message); }
            MelonLogger.Msg("[STEP] 7 AddPerkGroup");
            if (MiniLoader.DiagLean) try { Diag.DumpRegistryClassHistogram(24); }
            catch (Exception de) { MelonLogger.Warning("[DIAG] 直方图失败: " + de.Message); }
            AddPerkGroup();
            if (MiniLoader.DiagFull) try { Diag.ScanTypesReferencingPerks(); }
            catch (Exception se) { MelonLogger.Warning("[SCAN] 失败: " + se.Message); }
            MelonLogger.Msg("[STEP] 7.5 RegisterModPerksIntoTabGroups");
            try { Diag.RegisterModPerksIntoTabGroups(); }
            catch (Exception rpe) { MelonLogger.Error("[TAB] 登记失败: " + rpe); }
            MelonLogger.Msg("[STEP] 8 Init AllGUIDDict");
            foreach (var (id, uniqueIDScriptable) in AllGUIDDict)
            {
                uniqueIDScriptable.Init();
            }
            MelonLogger.Msg("[STEP] 9 done  总耗时=" + swAll.ElapsedMilliseconds + "ms"
                            + "  最后一段(5~9)=" + swStep.ElapsedMilliseconds + "ms");
            MelonLogger.Msg("[CREATE] 创建路径统计: shim=" + LoadArchMod.ShimCreatedCount
                            + " fallback-clone=" + LoadArchMod.FallbackCloneCount + " fallback-new=" + LoadArchMod.FallbackCreatedCount
                            + " | UseOwnCreationFallback=" + MiniLoader.UseOwnCreationFallback
                            + " SkipShimCreation=" + MiniLoader.SkipShimCreation);
            var allDataAfter = Diag.AllDataCount();
            MelonLogger.Msg("[ALLDATA] DataBase.AllData 条目数 " + allDataBefore + " → " + allDataAfter
                            + "（+ " + (allDataAfter - allDataBefore) + "）"
                            + " | 本次经 loader 加入 = " + LoadArchMod.AllDataAddedCount
                            + " | AddToGameDataBase=" + !MiniLoader.SkipGameDataBaseAdd);
            MelonLogger.Msg(Diag.SummaryLine());     // [DIAGSUM] 一行汇总（lean 下这是主判据）
            Diag.LogNameIndexSummary();              // [NAMEIDX-SUM] 名字索引命中汇总（替代逐条）
        }
        catch (Exception e)
        {
            MelonLogger.Error(e);
        }
    }

    /// <summary>打印上一段耗时并重启计时（诊断瘦身：一眼看出 33 秒花在哪）。</summary>
    private static void StepDone(string label, System.Diagnostics.Stopwatch sw)
    {
        try
        {
            MelonLogger.Msg("[STEP-T] " + label + " 耗时=" + sw.ElapsedMilliseconds + "ms");
            sw.Restart();
        }
        catch
        {
        }
    }

    private static void AddPerkGroup()
    {
        // 诊断：为什么「新特质看不到」——先看查表用的是什么键
        var dict = ItemDictionary(typeof(PerkGroup));
        MelonLogger.Msg("[PERK] ItemDictionary(PerkGroup) 条目=" + dict.Count
                        + " 键=[" + string.Join(", ", dict.Keys.Take(6)) + "]");

        // 关键修复：用 il2cpp 真实类名把游戏自带的 PerkGroup 从注册表里捞出来，按「名字」建索引。
        // 原版靠 Resources.FindObjectsOfTypeAll 把非 UniqueIDScriptable 的对象按 .name 注册；
        // 本机这条链被裁掉了，而 UniqueIDScriptable 注册表路径是按 GUID 注册的，
        // 于是 AddPerkGroup 按组名查表永远查不到 → 新特质挂不进任何特质组 → 玩家看不到。
        var byName = Diag.BuildPerkGroupIndex(out var scanned, out var matched);
        MelonLogger.Msg("[PERK] 注册表扫描: 共 " + scanned + " 条，真实类名=PerkGroup 的 " + matched
                        + " 个，建出名字索引 " + byName.Count + " 个");
        foreach (var kv in byName.Take(20))
            MelonLogger.Msg("[PERK]   组名索引: \"" + kv.Key + "\"");

        int req = 0, hit = 0, added = 0, miss = 0, err = 0;
        var hitNames = new HashSet<string>();
        foreach (var tuple in WaitForAddPerkGroup)
        {
            req++;
            try
            {
                PerkGroup target = null;
                if (dict.TryGetValue(tuple.Item1, out var group)) target = group as PerkGroup;
                if (target == null && byName.TryGetValue(tuple.Item1, out var g2)) target = g2;
                if (target == null)
                {
                    miss++;
                    if (miss <= 8)
                        MelonLogger.Warning("[PERK] 找不到特质组: \"" + tuple.Item1 + "\" 特质=" + Diag.NameOf(tuple.Item2));
                    continue;
                }

                hit++;
                hitNames.Add(Diag.NameOf(target));
                var il2CppReferenceArray = (Il2CppArrayBase<CharacterPerk>)target.PerksList;
                Il2CppSystem.Array.Resize(ref il2CppReferenceArray, target.PerksList.Length + 1);
                target.PerksList = (Il2CppReferenceArray<CharacterPerk>)il2CppReferenceArray;
                target.PerksList[^1] = tuple.Item2;
                added++;
            }
            catch (Exception ex)
            {
                err++;
                if (err <= 5)
                    MelonLogger.Warning("[PERK] 挂载异常: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        MelonLogger.Msg("[PERK] 请求=" + req + " 命中组=" + hit + " 已挂载=" + added
                        + " 找不到组=" + miss + " 异常=" + err);

        // 回读：挂载后每个组的 PerksList 长度
        foreach (var kv in byName.Take(12))
            try { MelonLogger.Msg("[PERK]   回读 组=\"" + kv.Key + "\" PerksList=" + (kv.Value.PerksList?.Length ?? -1)); }
            catch (Exception e) { MelonLogger.Warning("[PERK]   回读失败 " + kv.Key + ": " + e.Message); }
    }

    private static bool _initFlag;

    [HarmonyPostfix, HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.Init))]
    public static void GraphicsManagerInitPostfix(GraphicsManager __instance)
    {
            if (SkipGraphicsInitPostfix) return;   // [DIAG] 本轮跳过该补丁
        try
        {
            AddCardTabGroupPublic(__instance);

            AddBlueprintCardDataPublic(__instance);

            AddVisibleGameStatPublic(__instance);

            if (!_initFlag)
            {
                AddCardTabGroupOncePublic(__instance);

                CustomGameObjectFixedPublic();

                AddCardFilterGroupOncePublic();
                _initFlag = true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
    }

    public static void AddCardFilterGroupOncePublic()
    {
        var cardFilterGroupDict = new Dictionary<string, CardFilterGroup>();

        foreach (var ele in Resources.FindObjectsOfTypeAll(Il2CppType.Of<CardFilterGroup>()))
        {
            if (ele is CardFilterGroup cardFilterGroup)
                cardFilterGroupDict.Add(ele.name, cardFilterGroup);
        }

        foreach (var item in WaitForAddCardFilterGroupCard)
            if (cardFilterGroupDict.TryGetValue(item.Item1, out var filter))
                filter.IncludedCards.Add(item.Item2);
    }

    public static void CustomGameObjectFixedPublic()
    {
        foreach (var item in CustomGameObjectListDict)
            try
            {
                var transform = item.Value.transform.Find("Shadow/GuideFrame/GuideContentPage/Content/Horizontal");
                if (transform != null)
                    for (var i = 0; i < transform.childCount; i++)
                        Object.Destroy(transform.GetChild(i).gameObject);

                transform = item.Value.transform.Find("Shadow/GuideFrame");
                var fx = transform.gameObject.GetComponent(Il2CppType.Of<FXMask>()) as FXMask;
                if (fx != null) fx.enabled = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("CustomGameObjectFixed " + ex.Message);
            }
    }

    public static void AddCardTabGroupOncePublic(GraphicsManager instance)
    {
        foreach (var item in WaitForAddCardTabGroup)
            try
            {
                if (item.Obj is not CardTabGroup itemObj)
                    continue;

                itemObj.FillSortingList();

                if (!itemObj.name.StartsWith("Tab_"))
                    continue;

                if (itemObj.SubGroups.Count == 0)
                {
                    var json = item.CardData;
                    if (json != null && json.ContainsKey("BlueprintCardDataCardTabGroup") &&
                        json["BlueprintCardDataCardTabGroup"].IsString && !string.IsNullOrWhiteSpace(
                            json["BlueprintCardDataCardTabGroup"].ToString()))
                        foreach (var group in instance.BlueprintModelsPopup.BlueprintTabs)
                            if (group.name == json["BlueprintCardDataCardTabGroup"].ToString())
                            {
                                group.SubGroups.Add(itemObj);
                                group.FillSortingList();
                                break;
                            }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("AddCustomCardTabGroup " + ex.Message);
            }
    }

    public static void AddVisibleGameStatPublic(GraphicsManager instance)
    {
        foreach (var tuple in WaitForAddVisibleGameStat)
            try
            {
                // var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                // var StatList =
                //     instance.AllStatsList.GetType().GetField("Tabs", bindingFlags)
                //         .GetValue(instance.AllStatsList) as StatListTab[];
                var statList = instance.AllStatsList.Tabs;
                foreach (var list in statList)
                    if (list.name == tuple.Item1)
                    {
                        list.ContainedStats.Add(tuple.Item2);
                        break;
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("AddVisibleGameStat " + ex.Message);
            }
    }

    public static void AddCardTabGroupPublic(GraphicsManager instance)
    {
        foreach (var item in WaitForAddCardTabGroup)
            try
            {
                if (item.Obj is not CardTabGroup tabGroup)
                    continue;

                tabGroup.FillSortingList();

                if (!tabGroup.name.StartsWith("Tab_"))
                    continue;

                if (tabGroup.SubGroups.Count != 0)
                {
                    Il2CppArrayBase<CardTabGroup> il2CppReferenceArray = instance.BlueprintModelsPopup.BlueprintTabs;
                    Il2CppSystem.Array.Resize(ref il2CppReferenceArray,
                        instance.BlueprintModelsPopup.BlueprintTabs.Length + 1);
                    instance.BlueprintModelsPopup.BlueprintTabs =
                        (Il2CppReferenceArray<CardTabGroup>)il2CppReferenceArray;
                    instance.BlueprintModelsPopup.BlueprintTabs[^1] = tabGroup;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("AddCardTabGroup " + ex.Message);
            }
    }

    public static void AddBlueprintCardDataPublic(GraphicsManager instance)
    {
        foreach (var tuple in WaitForAddBlueprintCard)
            try
            {
                foreach (var group in instance.BlueprintModelsPopup.BlueprintTabs)
                    if (group.name == tuple.Item1)
                    {
                        group.ShopSortingList.Add(tuple.Item3);
                        foreach (var subGroup in group.SubGroups)
                            if (subGroup.name == tuple.Item2)
                            {
                                subGroup.IncludedCards.Add(tuple.Item3);
                                break;
                            }

                        break;
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("AddBlueprintCardData " + ex.Message);
            }
    }
}


