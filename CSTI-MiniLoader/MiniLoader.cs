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

            // mod 对象是否写进游戏主数据表（默认 true）
            var addEntry = cat.CreateEntry("AddToGameDataBase", true,
                "把 mod 对象加进游戏主数据表 DataBase.AllData（控制台/UI 列卡片需要）");
            SkipGameDataBaseAdd = !addEntry.Value;

            // 自带创建兜底（不依赖 CstiICallFix），默认 true
            var ownEntry = cat.CreateEntry("UseOwnCreationFallback", true,
                "找不到 ICallFix.RealShims.CreateLike 时用 il2cpp_object_new 自带兜底创建 mod 对象");
            UseOwnCreationFallback = ownEntry.Value;

            var skipShimEntry = cat.CreateEntry("SkipShimCreation", false,
                "禁用 ICallFix 的 shim 创建路径（只用自带兜底，便于两条路径对照验收）");
            SkipShimCreation = skipShimEntry.Value;

            // cfg 路径（MelonPreferences 在 UserData 下）+ 旧键兼容
            var cheatEntry = cat.CreateEntry("MaintainCheatLists", true,
                "在泵里把 mod 卡牌补进 CheatsManager.AllCards / GameManager.AllCards（控制台能搜到）");
            MaintainCheatLists = cheatEntry.Value;

            var gsmEntry = cat.CreateEntry("GSM_Apply", true,
                "是否应用 GameSourceModify（改造游戏原有卡牌，如精灵采摘/编织/缠线）");
            GsmApply = gsmEntry.Value;

            var gsmInline = cat.CreateEntry("GSM_InlineWrite", false,
                "值类型字段是否走托管属性 setter 安全通道（默认关；直写内存已撤销，会崩）");
            GsmInlineWrite = gsmInline.Value;

            var ssf = cat.CreateEntry("StructSetterFix", true,
                "内联结构字段用托管代理+属性 setter 写回（纯托管，无裸内存）");
            StructSetterFix = ssf.Value;

            var fillEntry = cat.CreateEntry("CheatListsTriggerFill", true,
                "mod 卡不在控制台列表时调用游戏 FillCards() 重填一次");
            CheatListsTriggerFill = fillEntry.Value;

            try
            {
                PrefPath = System.IO.Path.Combine(
                    MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
            }
            catch
            {
                PrefPath = null;
            }

            if (ApplyLegacyPrefs())
                MelonLogger.Msg("[PREF] 检测到旧键（GSM.Apply / GSM.InlineWrite），已按文件值覆盖一次");

            MelonLogger.Msg("[DIAGLVL] 诊断级别 = " + Diag + "（配置 CSTI_MiniLoader/DiagLevel）"
                            + " | AddToGameDataBase=" + !SkipGameDataBaseAdd
                            + " | UseOwnCreationFallback=" + UseOwnCreationFallback
                            + " | SkipShimCreation=" + SkipShimCreation + " | MaintainCheatLists=" + MaintainCheatLists + " | GSM.Apply=" + GsmApply + " | GSM.InlineWrite=" + GsmInlineWrite);
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
    /// 是否把 mod 对象加进**游戏自己的主数据表**（`GameLoad.Instance.DataBase.AllData`）。
    /// 由 MelonPreferences `CSTI_MiniLoader/AddToGameDataBase` 驱动，**默认 true**（= 加进去）。
    /// 历史：早期浅拷贝共享污染导致"事件选了没反应/没掉落"，"不写 AllData"只是当时的二分手段；
    /// 现在递归深拷贝 + 重开嵌套 warp 已根治（`[INVARIANT]` 长期 0 变化），故默认加回来
    /// —— 控制台/UI 列卡片很可能正是遍历这张表。
    /// </summary>
    public static bool SkipGameDataBaseAdd;

    /// <summary>
    /// 是否启用**自带创建兜底**（`LoadArchMod.CreateScriptableObjectViaShim` 的第二条路径）：
    /// 反射找不到 `CstiICallFix.RealShims.CreateLike` 或它返回 null 时，用真实 il2cpp 类
    /// `il2cpp_object_new` 建空实例 —— 让 loader **不再硬依赖 CstiICallFix**。
    /// MelonPreferences：`CSTI_MiniLoader/UseOwnCreationFallback`（默认 true）。
    /// </summary>
    public static bool UseOwnCreationFallback = true;

    /// <summary>是否允许走 ICallFix 的 shim 创建路径（`CSTI_MiniLoader/SkipShimCreation`，默认 false=允许）。</summary>
    public static bool SkipShimCreation;

    /// <summary>
    /// 是否在泵里幂等维护作弊控制台的两张卡表（`CheatsManager.AllCards` / `GameManager.AllCards`），
    /// 让 mod 卡牌能在控制台里搜到。MelonPreferences：`CSTI_MiniLoader/MaintainCheatLists`（默认 true）。
    /// </summary>
    public static bool MaintainCheatLists = true;

    /// <summary>总开关：是否应用 GameSourceModify（改造游戏原有卡牌）。`CSTI_MiniLoader/GSM.Apply`（默认 true）。
    /// 关掉即完全不碰游戏对象（出问题时的一键回退）。</summary>
    public static bool GsmApply = true;

    /// <summary>值类型字段的**安全通道**：是否用托管属性 setter 去填（默认 **false**）。
    /// `CSTI_MiniLoader/GSM.InlineWrite`。真机教训：直写非托管内存会 SIGSEGV，已永久撤销；
    /// 这里只允许 `prop.SetValue`，失败就跳过。</summary>
    public static bool GsmInlineWrite;

    /// <summary>
    /// 内联结构字段是否走"托管代理 + 属性 setter 写回"（`CSTI_MiniLoader/StructSetterFix`，默认 **true**）。
    /// 纯托管：`prop.GetValue` → 在代理上 warp → `prop.SetValue` 整块写回；**无偏移、无 memcpy、无裸内存**。
    /// 关掉即回到"内联结构一律跳过"的老行为。
    /// </summary>
    public static bool StructSetterFix = true;

    /// 通用判据，不按 mod/卡名挑选）。</summary>


    /// <summary>
    /// mod 卡不在控制台列表里时，是否调用游戏自己的 `CheatsManager.FillCards()` 重填一次（最多 3 次）。
    /// MelonPreferences：`CSTI_MiniLoader/CheatListsTriggerFill`（默认 true）。
    /// </summary>
    public static bool CheatListsTriggerFill = true;

    /// <summary>是否把 LoadAndInit 延后到注册表就绪之后（推荐 true）。</summary>
    public static bool DeferredInit;

    /// <summary>MelonPreferences.cfg 的绝对路径（供"文件里到底有没有这个键"判据）。</summary>
    public static string PrefPath;

    /// <summary>文件里是否有该键（true=读的是文件值；false=用的是代码默认值）。</summary>
    public static string PrefFileHas(string key)
    {
        var raw = PrefFileRaw(key);
        return raw == null ? "无(默认)" : "有=" + raw;
    }

    /// <summary>从 MelonPreferences.cfg 里按 `key = value` 取原始值（支持带引号的键名，如旧的 "GSM.InlineWrite"）。</summary>
    public static string PrefFileRaw(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(PrefPath) || !System.IO.File.Exists(PrefPath)) return null;
            foreach (var line in System.IO.File.ReadAllLines(PrefPath))
            {
                var s = line.Trim();
                if (s.Length == 0 || s[0] == '[' || s[0] == '#') continue;
                var eq = s.IndexOf('=');
                if (eq <= 0) continue;
                var k = s.Substring(0, eq).Trim().Trim('"');
                if (k == key) return s.Substring(eq + 1).Trim();
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[MiniLoader] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return null;
    }

    /// <summary>旧键（带点号）兼容：若文件里有 `GSM.Apply`/`GSM.InlineWrite` 就按它覆盖一次。</summary>
    private static bool ApplyLegacyPrefs()
    {
        var changed = false;
        var a = PrefFileRaw("GSM.Apply");
        if (a != null && bool.TryParse(a, out var av)) { GsmApply = av; changed = true; }
        var i = PrefFileRaw("GSM.InlineWrite");
        if (i != null && bool.TryParse(i, out var iv)) { GsmInlineWrite = iv; changed = true; }
        return changed;
    }
    /// <summary>[BUILD] 特性标记：列出本轮全部关键改动，便于"运行的代码 = 我们改的代码"一眼核对。</summary>
    public const string BuildFeatures =
        "dedup+fieldapi+formdispatch+nostruct-oracle+nocap+events-driven"
        + "+struct-member-fallback(prop→field)+managed-struct-warp(scalar)+commonSetFld-direct-write";

    /// <summary>[BUILD] 编译期源码路径（`[CallerFilePath]` 由编译器写死进 DLL）。</summary>
    private static string BuildSourcePath(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        => path + ":" + line;
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
        // ★ [BUILD] 构建身份行（**第一行**）—— 唯一可靠的"运行的代码 = 我们改的代码"证明：
        //   源码路径用 `[CallerFilePath]`（编译期写死在 DLL 里 ✓），再加程序集文件时间（= 构建时间 ✓）。
        //   以后一看到这行就知道设备上跑的是哪份源码、哪个 sha 的构建。
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var loc = asm.Location;
            var built = System.IO.File.Exists(loc) ? System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss") : "?";
            var ver = asm.GetName().Version?.ToString() ?? "?";
            var sha = "?";
            try
            {
                using var fs = System.IO.File.OpenRead(loc);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                sha = Convert.ToHexString(sha256.ComputeHash(fs))[..16];
            }
            catch (Exception __e)
            {
                MelonLogger.Warning("[BUILD] 计算自身 sha 失败: " + __e.Message);
            }

            MelonLogger.Msg("[BUILD] CSTI-MiniLoader 版本=" + ver
                            + " 特性=" + BuildFeatures
                            + " 源码路径=" + BuildSourcePath()
                            + " 构建时间=" + built
                            + " 自身sha前16=" + sha
                            + " 程序集=" + loc);
        }
        catch (Exception __e)
        {
            MelonLogger.Warning("[BUILD] 身份行打印失败: " + __e.GetType().Name + " " + __e.Message);
        }

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

