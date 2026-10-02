using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CSTI_MiniLoader.LoadUtil;
using CSTI_MiniLoader.Patchers;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CSTI_MiniLoader;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class MiniLoader : MelonMod
{
    public struct ScriptableObjectPack
    {
        public ScriptableObject? Obj;
        public readonly string CardDirOrGuid;
        public string CardPath;
        public readonly string ModName;
        public readonly KVProvider? CardData;

        public ScriptableObjectPack(ScriptableObject? obj,
            string cardDirOrGuid,
            string cardPath,
            string modName,
            KVProvider? cardData)
        {
            Obj = obj;
            CardDirOrGuid = cardDirOrGuid;
            CardPath = cardPath;
            ModName = modName;
            CardData = cardData;
        }
    }

    public const string Version = "0.0.2";
    public static readonly Dictionary<Type, Dictionary<string, object>> AllItemDictionary = new();
    public static readonly Dictionary<string, Dictionary<string, string>> AllLuaFiles = new();
    public static readonly List<(string LocalName, string LocalContent)> WaitForLoadCSVList = new();
    public static readonly Dictionary<string, UniqueIDScriptable> AllGUIDDict = new();
    public static readonly Dictionary<string, ScriptableObject> AllScriptableObjectDict = new();
    public static readonly List<ScriptableObjectPack> WaitForWarpperEditorNoGuidList = new();
    public static readonly List<ScriptableObjectPack> WaitForWarpperEditorGameSourceGUIDList = new();
    public static readonly Dictionary<string, ScriptableObjectPack> WaitForWarpperEditorGuidDict = new();
    public static readonly List<ScriptableObjectPack> WaitForAddCardTabGroup = new();
    public static readonly List<ScriptableObjectPack> WaitForAddJournalPlayerCharacter = new();
    public static readonly List<ScriptableObjectPack> WaitForAddDefaultContentPage = new();
    public static readonly List<ScriptableObjectPack> WaitForAddMainContentPage = new();
    public static readonly List<GuideEntry> WaitForAddGuideEntry = new();
    public static readonly List<Tuple<string, string, CardData>> WaitForAddBlueprintCard = new();
    public static readonly List<Tuple<string, CardData>> WaitForAddCardFilterGroupCard = new();
    public static readonly List<Tuple<string, CharacterPerk>> WaitForAddPerkGroup = new();

    /// <summary>
    /// mod 自己新增的 CharacterPerk（warp 阶段收集）。
    /// 本版本游戏里特质显示由 PerkTabGroup.ContainedPerks 决定（CharacterPerk 已经没有 PerkGroup 字段了），
    /// 所以这些特质必须在 warp 之后手动挂进 PerkTabGroup。
    /// </summary>
    public static readonly List<CharacterPerk> ModPerks = new();
    public static readonly List<Tuple<string, GameStat>> WaitForAddVisibleGameStat = new();
    public static readonly List<ScriptableObjectPack> WaitForMatchAndWarpperEditorGameSourceList = new();
    public static readonly Dictionary<string, Dictionary<string, CardData>> AllCardTagGuidCardDataDict = new();
    public static readonly Dictionary<string, GameObject> CustomGameObjectListDict = new();
    public static readonly Dictionary<string, ContentDisplayer> CustomContentDisplayerDict = new();
    public static readonly HarmonyLib.Harmony HarmonyIns = new("zender.CSTI-MiniLoader");

    public static void RegObj(string id, object o, Type? type)
    {
        if (ItemDictionary(type).ContainsKey(id)) return;
        ItemDictionary(type)[id] = o;
        if (type != null && type.IsSubclassOf(typeof(UniqueIDScriptable)))
        {
            AllGUIDDict[id] = (UniqueIDScriptable)o;
        }

        if (type != null && type.IsSubclassOf(typeof(ScriptableObject)))
        {
            AllScriptableObjectDict[id] = (ScriptableObject)o;
        }
    }

    public static Dictionary<string, object> ItemDictionary(Type? type)
    {
        if (type == null)
        {
            return new Dictionary<string, object>();
        }

        if (AllItemDictionary.TryGetValue(type, out var dictionary))
        {
            return dictionary;
        }

        var objects = new Dictionary<string, object>();
        AllItemDictionary[type] = objects;
        return objects;
    }

    /// <summary>
    /// 诊断级别（MelonPreferences 可配：`CSTI_MiniLoader/DiagLevel` = off|lean|full）。
    /// · full：逐对象/逐字段/逐命中明细全打（排障用，冷启动明显变慢）；
    /// · lean：只留**汇总**（`[NAMEIDX]` / `[TAGS-SUM]` / `[EFFECT2-SUM]` / `[INVARIANT]` / `[STEP] 耗时`），跳过探针与逐条明细；
    /// · off ：除错误与 `[STEP]`/`[自检]` 要点外不打诊断。
    /// 默认 full（用户确认可以关掉后再改默认）。
    /// </summary>
    public enum DiagLevel
    {
        Off,
        Lean,
        Full
    }

    public static DiagLevel Diag = DiagLevel.Full;
    public static bool DiagFull => Diag == DiagLevel.Full;
    public static bool DiagLean => Diag != DiagLevel.Off;

    private static void LoadDiagLevel()
    {
        try
        {
            var cat = MelonPreferences.CreateCategory("CSTI_MiniLoader");
            var entry = cat.CreateEntry("DiagLevel", "full", "诊断级别 (off|lean|full)");
            var v = (entry.Value ?? "full").Trim().ToLowerInvariant();
            Diag = v switch
            {
                "off" => DiagLevel.Off,
                "lean" => DiagLevel.Lean,
                _ => DiagLevel.Full
            };
            MelonLogger.Msg("[DIAGLVL] 诊断级别 = " + Diag + "（配置 CSTI_MiniLoader/DiagLevel）");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DIAGLVL] 读取配置失败，按 full 运行: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>诊断开关：跳过 Harmony PatchAll（只加载 DLL）。</summary>
    public static bool SkipHarmonyPatchAll = true;

    /// <summary>
    /// E2 探针：给 GetCollectionDropsReport / FillDropList 挂**只打日志**的 Postfix。
    /// 已拿到结论（20:57 轮：FillDropList 命中 20 次、GetCollectionDropsReport 命中 0 次），
    /// 且随后出现一次 SIGSEGV，为排除"是我们的 Postfix 引起"而先关掉做崩溃二分。
    /// </summary>
    public static bool EnableE2Probe = false;

    /// <summary>诊断开关：跳过 LoadPatchMain.LoadAndInit()。</summary>
    public static bool SkipInit = false;

    /// <summary>
    /// 二分 2a 开关：不把 mod 对象加进游戏自己的主数据表（`GameLoad.Instance.DataBase.AllData`）。
    /// 游戏的事件/掉落结算会遍历它，撞上「空容器 + 字段不全」的克隆卡就可能中途异常。
    /// mod 对象仍然进 AllGUIDDict / 自建字典 / 游戏注册表（Init 时）。
    /// </summary>
    public static bool SkipGameDataBaseAdd = true;

    /// <summary>是否把 LoadAndInit 延后到注册表就绪之后（推荐 true）。</summary>
    public static bool DeferredInit;

    private static bool _initDone;

    /// <summary>由 HookFree 在注册表就绪后调用一次。</summary>
    public static void RunDeferredInit()
    {
        if (_initDone) return;
        _initDone = true;
        try
        {
            MelonLogger.Msg("Call LoadPatchMain.LoadAndInit (deferred)");
            LoadPatchMain.LoadAndInit();
        }
        catch (Exception e) { MelonLogger.Error("LoadAndInit 失败: " + e); }
    }

    public static bool InitDone => _initDone;

    private static bool _hookFreeLogged;

    public override void OnUpdate()
    {
        if (SkipHarmonyPatchAll)   // 只有在“不 hook”模式下才需要轮询注入
        {
            if (!_hookFreeLogged)
            {
                _hookFreeLogged = true;
                MelonLogger.Msg("[HOOKFREE] OnUpdate 轮询已启动（免 hook 注入模式）");
            }
            HookFree.Tick();
        }
    }

    public override void OnInitializeMelon()
    {
        LoadDiagLevel();

        if (SkipHarmonyPatchAll)
        {
            MelonLogger.Warning("[DIAG] 跳过 HarmonyIns.PatchAll(typeof(LoadPatchMain))");
            // 支持模块的 Update 泵是坏的（SM_Component.Create() 被我们打断），
            // 所以用「安全每帧方法 + Harmony Postfix」自建一个泵来驱动免 hook 注入。
            if (Pump.Install())
            {
                Pump.Register(HookFree.Tick);
            }
        }
        else
        {
            // 2024-02-18 作者在此处留了 throw new NotImplementedException("这不现实") 宣告放弃；
            // 现恢复真实初始化，用于逐步定位并修复运行期问题。
            MelonLogger.Msg("[DIAG] 执行 HarmonyIns.PatchAll(typeof(LoadPatchMain))");
            HarmonyIns.PatchAll(typeof(LoadPatchMain));
            MelonLogger.Msg("[DIAG] PatchAll 完成");
        }

        if (EnableE2Probe)
        {
            try
            {
                MelonLogger.Msg("[E2] 安装 E2 只读探针（只打日志，不改返回值/不写游戏对象）");
                E2Probe.Install(HarmonyIns);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[E2] 安装失败: " + e.GetType().Name + " " + e.Message);
            }
        }

        if (SkipInit)
        {
            MelonLogger.Warning("[DIAG] 跳过 LoadPatchMain.LoadAndInit()");
        }
        else
        {
            // 延后到「游戏注册表就绪」之后执行：原版顺序是 LoadGameResource → 读 mod 包 → Warpper，
            // 但注册表要等游戏资源加载完才有内容，所以在 HookFree 的每帧 tick 里触发。
            DeferredInit = true;
            MelonLogger.Msg("[DIAG] LoadAndInit 已延后，等注册表就绪（HookFree 驱动）");
        }
    }
}

