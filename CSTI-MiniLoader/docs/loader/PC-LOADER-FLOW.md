# PC 版 CSTI ModLoader 加载流程精研（交接文档）

> **任务性质**：只读研究。本文不含任何代码改动，未修改任何被研究的仓库、未提交、未触碰设备。
>
> **证据约定**
> - `[PC源码:<文件>:<行>]` —— 直接读 PC loader 源码得来，可逐行核对。
> - `[PC游戏IL:<类型>::<方法> IL_xxxx]` —— 用 Mono.Cecil 离线反编译 `Card Survival - Tropical Island_Data\Managed\Assembly-CSharp.dll` 得来。
> - `[实测日志]` —— 本机已有的真实运行日志/数据文件行。
> - `[推测未验证]` —— 由命名/签名推断，未直接取到证据。
> - `需移动端 il2cpp 校核` —— PC 与 Android(il2cpp) 两版可能不同的点，**不要当结论用**。
>
> **勘误**：本文档在交付前已存在一份早前草稿（同路径，14:41 版本）。该草稿含若干**与源码不符**的结论，已在 **附录 C** 逐条列出并纠正，请以本文为准。

---

## 0. 先纠正一个前提：PC 源码的"真身"在哪

任务描述里假定「PC 原版实现应在 `main`/上游分支」。实测结果与此**不符**，必须先说清楚，否则会读错代码：

| 引用 | 提交 | 日期 | 内容 | 结论 |
|---|---|---|---|---|
| `master`（本地）= `origin/master` | `f5bde01` | 2023-03-06 | `CSTI-ModLoader/` 只有 `ModLoader.cs`(1652 行) + `WarpperFunction.cs`(464 行) | **陈旧，是 v2.x 之前的旧版**，不是当前 PC 实现 |
| **`73dc02f`（"2.3.6.35"）** | `73dc02f` | **2024-02-17** | `CSTI-ModLoader/`(2033 行 `ModLoader.cs` + `LoaderUtil/` + `ExportUtil/` + `FFI/` + `UI/` + `Updater/`) + `DynamicModLoader/` | **这才是 PC 当前实现** |
| `android-06-port`（HEAD `dbf275e`） | `dbf275e` | 2026-10-03 | 上述 PC 代码 **+ 我们新增的 `CSTI-MiniLoader/`** | 我们的移植分支 |

验证方式（只读）：

```
git merge-base master android-06-port      → f5bde01        # master 是 android-06-port 的祖先
git diff --stat HEAD 73dc02f -- CSTI-ModLoader DynamicModLoader
  → CSTI-ModLoader/ModLoader.csproj | 4 ++--                 # 唯一差异，2 行
  → (CSTI-ModLoader/*.cs 与 DynamicModLoader/*.cs 零差异)
```

**因此：`D:\RiderProjects\CSTI-ModLoader\CSTI-ModLoader\**` 的工作区文件 = PC 上游 `2.3.6.35` 的字节级同源副本，可以直接用 `read` 读，**行号即本文所有 `[PC源码:...]` 的依据**。**
游戏里部署的 `ModLoader.dll` 的 `FileVersion` 也正好是 `2.3.6.35`（`F:\...\BepInEx\plugins2\CSTI-ModLoader\ModLoader\ModLoader.dll`），版本对得上。

> ⚠️ **顺带发现（与本次问题域相关，请务必知悉）**：现有 PC 安装里 mod 都在 `BepInEx\plugins2\`，但 **2.3.6.35 的 loader 源码里根本没有 `plugins2` 这个字符串**：
> - `LoadMods` / `LoadModsFromZip` / `LoadPreData.LoadData` 只读 `Path.Combine(Paths.BepInExRootPath, "plugins")` `[PC源码:CSTI-ModLoader/ModLoader.cs:1764, 505, 350]`；
> - `LoadArchMod` 只读 `Paths.PluginPath`（BepInEx 5.4.21 下 = `BepInEx\plugins`）`[PC源码:CSTI-ModLoader/ExportUtil/LoadArchMod.cs:22]`；
> - 全仓库 grep `plugins2` → 0 命中；`BepInEx.dll` / `BepInEx.Preloader.dll` 字符串里无 `plugins2`；对 `F:\...\BepInEx` 全目录做**二进制**扫描 `plugins2` → 0 命中；
> - 现有 `BepInEx\LogOutput.log`（9 个插件）与 `plugins\` 目录内容一一对应，**ModLoader 不在其中**；2024 年的旧日志 `LogOutput.log.1` 显示当时 ModLoader 是从 `BepInEx\plugins\CSTI-ModLoader\ModLoader\ModLoader.dll` 加载的。
>
> **结论（`[实测]`）：当前 F: 盘这套 `plugins2\` 布局不是 2.3.6.35 期望的布局，BepInEx 5.4.21 不会扫描它。** 如果要让 PC 版真正跑起来做对照实验，`plugins2\` 需要改名/复制回 `plugins\`。这条与移动端移植无关，但会影响"用 PC 端做对照实验"这件事，请先确认。

---

## ① 文字版总时序

```
[BepInEx Chainloader]
  └─ 递归扫 BepInEx\plugins\**\*.dll → 找到 ModLoader.dll（2.3.6.35）
       │
       ├─ (A) 静态构造 ModLoader..cctor()      [ModLoader.cs:71-82]
       │     · LuaSupportRuntime.Init(SpriteDict, AllLuaFiles)
       │     · NormalPatcher.DoPatch(HarmonyInstance)   ← 拉起 CSTI_LuaActionSupport.dll 的一大批补丁
       │
       ├─ (B) Awake()                          [ModLoader.cs:225-352]
       │     · 自动更新协程 / MainUI.CreatePanel() / PostSpriteLoad.CompressOnLate() 协程
       │     · 若存在 EncounterPopup 类型 → MainPatcher.DoPatch()   ← CSTI-ChatTreeLoader.dll
       │     · 预建 AllScriptableObjectWithoutGuidTypeDict 的所有 [Serializable] ScriptableObject 桶
       │     · 安装 5 个 Harmony 补丁：
       │         UniqueIDScriptable.ClearDict      (prefix)   ← ★ 核心时机
       │         LocalizationManager.LoadLanguage  (postfix)
       │         GuideManager.Start                (prefix)
       │         GraphicsManager.Init              (postfix)
       │         FXMask.Awake
       │     · 找 Assembly-CSharp → GameSourceAssembly
       │     · LoadPreData.LoadData(<BepInExRoot>\plugins)      ← "预热"：注册 ModPacks + 异步读图 + 异步读 JSON
       │
       ├─ (C) Start()                          [ModLoader.cs:184-189]
       │     · 中文字体 AssetBundle 协程 + CompatibleCheck.MainCheck()
       │
       └─ (D) Update()/OnGUI()                 [ModLoader.cs:1877, 1918]
             · 只做 UI：模组管理器(Ctrl+Tab)、加载完成提示、自动更新重启

==================== 游戏开始加载数据（关键分水岭） ====================
[游戏] GameLoad.<AwakeWithLoadingScreen>d__53.MoveNext()
   IL_0074: call UniqueIDScriptable::ClearDict()
        ↑↑↑ 我们的 Harmony PREFIX 在这里整体跑完 ↑↑↑
        ┌──────────────────────────────────────────────────────────────┐
        │ UniqueIDScriptableClearDictPrefix()   [ModLoader.cs:1747-1802]│
        │  0. if(已经跑过) return                (SimpleOnce 幂等)       │
        │  1. LoadGameResource()                 [1760] 收编全部原版 SO │
        │  2. LoadArchMod.LoadAllArchMod()       [1762] *.modArch* 归档 │
        │  3. LoadMods(<root>\plugins)           [1764] 文件夹型 mod    │
        │  4. LoadModsFromZip()                  [1766] plugins\*.zip   │
        │  5. PostSpriteLoad.BeginCompress=true  [1767] 启动贴图解码    │
        │  6. LoadPreData.LoadFromPreLoadData()  [1769] 消化预热结果→建卡│
        │  7. LoadEditorScriptableObject()       [1771] ScriptableObject/│
        │  8. DoWarpperLoader.WarpperAllEditorMods()          [1777]    │
        │  9. DoWarpperLoader.WarpperAllEditorGameSrouces()   [1779] GSM│
        │ 10. DoWarpperLoader.MatchAndWarpperAllEditorGameSrouce()[1781]│
        │ 11. AddPerkGroup()                                  [1786]    │
        │  finally: PostSpriteLoad.CanEnd = true              [1800]    │
        └──────────────────────────────────────────────────────────────┘
   IL_0079..IL_00C0: for (i = 0; i < GameLoad.DataBase.AllData.Count; i++)
                        if (AllData[i] != null) AllData[i].Init();
        ↑↑↑ 游戏自己替我们给"刚加进去的 mod 对象"调用 Init() ↑↑↑
             · UniqueIDScriptable.Init()  →  RegisterID()  →  AllUniqueObjects[UniqueID] = this
             · CardData.Init()            →  base.Init() → FillDropsList() → CachedFilter.Clear()
[游戏] 后续各 Manager 初始化时，补做 UI/索引类注册：
   LocalizationManager.LoadLanguage (postfix) → LoadLocalization()          [1804]
   GuideManager.Start               (prefix)  → LoadGuideEntry + AddPlayerCharacter [1816]
   GraphicsManager.Init             (postfix) → AddCardTabGroup / AddBlueprintCardData /
                                                AddVisibleGameStat / AddCardTabGroupOnce /
                                                CustomGameObjectFixed / AddCardFilterGroupOnce [1832]
   PostSpriteLoad.CompressOnLate()  (协程)    → 贴图队列排空后全量 CardGraphics.Setup() [PostSpriteLoad.cs:192]
```

**一句话**：PC 的加载是"**卡在游戏清空注册表的那一瞬间，把 mod 对象塞进 `DataBase.AllData`，然后让游戏自己的循环去做 `Init()`**"。loader 自己**从不调用 `Init()`**（grep `\.Init()` 在 PC 源码中 0 命中）。

---

## ② 八条必答（带代码级证据）

### 1. 入口与时机

**入口类**：`public class ModLoader : BaseUnityPlugin` `[PC源码:CSTI-ModLoader/ModLoader.cs:65]`
插件特性：`[BepInPlugin("Dop.plugin.CSTI.ModLoader", "ModLoader", ModVersion)]` `[:61]`，`ModVersion = "2.3.6.35"` `[:67]`，并且 **`[BepInDependency("zender.LuaActionSupport.LuaSupportRuntime")]` `[:62]`**（硬依赖，不是可选）。

| 回调 | 行 | 做什么 |
|---|---|---|
| 静态构造 `.cctor()` | `:71-82` | `LuaSupportRuntime.Init(SpriteDict, AllLuaFiles)`；`NormalPatcher.DoPatch(HarmonyInstance)`（拉起 Lua 动作补丁层）；整体 try/catch 只 `LogWarning` |
| `Awake()` | `:225-352` | 见下方分解 |
| `Start()` | `:184-189` | `StartCoroutine(FontLoader())`（内嵌 AssetBundle 的中文字体，挂成所有 TMP 字体的 fallback）+ `CompatibleCheck.MainCheck()` |
| `Update()` | `:1877` | 只处理模组管理器热键/自动更新重启；无加载逻辑 |
| `OnGUI()` | `:1918` | 模组管理器窗口、加载成功提示、更新成功提示 |

`Awake()` 分解：

- `:230` `StartCoroutine(AutoUpdate.UpdateModIfNecessary())`
- `:237` `MainUI.CreatePanel()`，`:238-242` 摆放并隐藏背景面板
- `:244-245` `PostSpriteLoad.CompressOnLate()` 协程启动（贴图延迟解码）
- `:246-249` 注册两个配置项：`SetTexture2ReadOnly`、`TexCompatibilityMode`
- `:251-255` `if (AccessTools.TypeByName("EncounterPopup") != null) { MainPatcher.DoPatch(HarmonyInstance); HasEncounterType = true; }`
- `:257-268` 遍历 `AccessTools.AllTypes()`，把所有带 `[Serializable]` 的 `ScriptableObject` 子类预先建桶
- `:270` `PluginVersion = Version.Parse(Info.Metadata.Version.ToString())`
- **`:276-281` `HarmonyInstance.Patch(AccessTools.Method(typeof(UniqueIDScriptable), "ClearDict"), prefix: UniqueIDScriptableClearDictPrefixMethod)`** ← 核心时机
- `:290-295` patch `LocalizationManager.LoadLanguage` postfix
- `:304-309` patch `GuideManager.Start` prefix
- `:319-324` patch `GraphicsManager.Init` postfix
- `:333-334` patch `FXMask.Awake`（`FixFXMaskAwake`，返回 bool 的 prefix，定义在 `:1535`）
- `:342-347` 从 `AppDomain.CurrentDomain.GetAssemblies()` 里找名为 `Assembly-CSharp` 的程序集 → `GameSourceAssembly`（后续所有"游戏类型"遍历都以它为界）
- `:350` `LoadPreData.LoadData(Path.Combine(Paths.BepInExRootPath, "plugins"))` ← 预热入口

**"什么时候算可以加载 mod" = `UniqueIDScriptable.ClearDict` 的前缀。**

为什么是这个点，而不是"等场景"或"等 GameManager"——游戏侧的 IL 给了确定答案
`[PC游戏IL:GameLoad/<AwakeWithLoadingScreen>d__53::MoveNext]`：

```
IL_0074: call System.Void UniqueIDScriptable::ClearDict()
IL_0079: ldc.i4.0                        ; i = 0
IL_007B: br.s IL_00af
IL_007D: ... GameLoad::DataBase → GameDataBase::AllData → get_Item(i)
IL_008E: call UnityEngine.Object::op_Implicit
IL_0093: brfalse.s IL_00ab               ; null 就跳过
IL_00A6: callvirt System.Void UniqueIDScriptable::Init()      ; ★ 游戏替我们 Init
IL_00AB: ldloc.2 / ldc.i4.1 / add / stloc.2                   ; i++
IL_00AF: ... AllData::get_Count()
IL_00C0: blt.s IL_007D
```

而注册链是 `[PC游戏IL:UniqueIDScriptable::Init]` = 一句 `call UniqueIDScriptable::RegisterID()`；
`[PC游戏IL:UniqueIDScriptable::RegisterID]` = `if(!AllUniqueObjects.ContainsKey(UniqueID)) AllUniqueObjects.Add(UniqueID, this); else if (AllUniqueObjects[UniqueID] != this) Duplicates.Add(this);`
`[PC游戏IL:CardData::Init]` = `base.Init()` → `FillDropsList()` → `CachedFilter.Clear()`。

所以 PC 的时序设计意图非常明确（与更新历史 v1.3.2 那条"将Mod载入从 `GameLoadLoadGameDataPostfix` 移动至 `UniqueIDScriptableClearDictPrefix`，由 GameLoad 完成 Init"完全吻合 `[实测:plugins2\CSTI-ModLoader\更新历史&使用说明.txt]`）：
**在 `ClearDict()` 之前把对象 append 进 `DataBase.AllData`，紧接着游戏的 for 循环就会对它们调用 `Init()`。**
`ClearDict` 本身只做两件事（`[PC游戏IL:UniqueIDScriptable::ClearDict]` = `AllUniqueObjects.Clear(); LoadedIDs.Clear();`），所以我们的前缀跑在"字典被清空"之前也无所谓——我们只往 `AllData` 里加对象，不依赖那两个字典。

> 这正是 Android 移植最大的结构性差异：移动端**没有**这个钩子（`LoadPatchMain.cs:321` 的 `[HarmonyPrefix, HarmonyPatch(typeof(UniqueIDScriptable), nameof(UniqueIDScriptable.ClearDict))]` 是注释掉的），只能轮询 + 自己调 `Init()`（见 ⑤-4）。

---

### 2. 发现（目录/扩展名/元数据）

共 **4 条发现通道**，其中 3 条在 `UniqueIDScriptableClearDictPrefix` 内被触发，1 条在 `Awake()` 里预热。

**通道 1：归档文件 `.modArch*`** — `[PC源码:CSTI-ModLoader/ExportUtil/LoadArchMod.cs:20-63]`

```csharp
Directory.EnumerateFiles(Paths.PluginPath, "*.modArch_V3", SearchOption.AllDirectories)   // :22
Directory.EnumerateFiles(Paths.PluginPath, "*.modArch_V2", SearchOption.AllDirectories)   // :36
Directory.EnumerateFiles(Paths.PluginPath, "*.modArch",    SearchOption.AllDirectories)   // :50
```
- **递归**（`AllDirectories`，不是 `TopDirectoryOnly`）扫描整个 `BepInEx\plugins`。
- 二进制容器：`BinaryReader.ReadString()` 读 mod 名 `[:71]`，然后循环读"块名"直到 `_End_` `[:88-92]`。
- 块分派 `LoadModArchBLK` `[:99-135]`：`ImgBLK`(v2 起走 `LoadImgBLK_V2`)、`AudioBLK`、`LocalBLK`、`JsonsBLK`(v3 走 `LoadJsonsBLK_V3`)、`LuaBLK`；每块内部是 LZ4 压缩的多段数据 `[:117-128]`。
- `JsonsBLK` 里每条记录带一个**路径清单** `listStr`，用它决定怎么解释 `[:422-549]`：
  - `listStr[0] == "ModInfo.json"` → 跳过 `[:428]`
  - `listStr[0] == "ScriptableObject"` → 非 `UniqueIDScriptable` 的 SO，`listStr[1]` 是类型名 `[:432-475]`
  - `listStr[0] == "GameSourceModify"` → 按文件名当 GUID 收进 GSM 队列 `[:476-484]`
  - 其它 → `listStr[0]` 与 `UniqueIDScriptable` 子类**按类型名匹配** `[:487-488]`，`listStr.Last()` 的文件名当对象名 `[:489]`

**通道 2：文件夹型 mod** — `[PC源码:CSTI-ModLoader/ModLoader.cs:922-1283]`

```csharp
var dirs = Directory.GetDirectories(mods_dir);                       // :926  只一层！
foreach (var dir in dirs) {
    if (!File.Exists(CombinePaths(dir, "ModInfo.json"))) continue;   // :930  ★ 没有 ModInfo.json 就不是 mod
```
`mods_dir` = `Path.Combine(Paths.BepInExRootPath, "plugins")` `[:1764]`。
目录约定（每个 `UniqueIDScriptable` 子类一个同名顶层目录；类型名来自 `GameSourceAssembly`）：

| 目录 | 内容 | 代码 |
|---|---|---|
| `ModInfo.json` | **必需**，mod 元数据 | `:930`, `:939-942` |
| `Resource/*.ab` | Unity AssetBundle，里面的 `Sprite`/`AudioClip` 全收 | `:977-1005` |
| `Resource/Audio/*.{wav,mp3,ogg}` | 音频 | `:1015-1055` |
| `Resource/Picture/*.{png,jpg,jpeg,dds}` + `Resource/Dxt/*.dds` | 图片（**异步**，见预热通道） | `LoaderUtil/LoadPreData.cs:138-146`、`ImageEntry` `PostSpriteLoad.cs:13-39` |
| `Localization/*.csv` | 本地化（靠文件名含 `SimpCn`/`SimpEn` 区分语言） | `:1132-1145`；应用在 `LoadLocalization` `:1321-1365` |
| `ScriptableObject/<TypeName>/**/*.json` | **非** `UniqueIDScriptable` 的自建 SO | `:1067-1121` |
| `<TypeName>/**/*.json`（`CardData`/`CharacterPerk`/`GameStat`/`Objective`/`SelfTriggeredAction`/`Encounter`/`PlayerCharacter`/`PerkGroup`…） | `UniqueIDScriptable` 子类，**在预热阶段异步读**，在阶段 6 建对象 | `LoaderUtil/LoadPreData.cs:156-157` → `ResourceLoadHelper.cs:41-84` |
| `GameSourceModify/**/<GUID>.json` | 改造原版卡，**文件名（去扩展名）= 目标 GUID** | `:1252-1270` |
| `JsonnetLib/` | jsonnet 运行库路径 | `ResourceLoadHelper.cs:45-49` |
| `.jsonnet` | 同 `.json`，但先过 Jsonnet 求值 | `ResourceLoadHelper.cs:68-70`、`LoadPreData.cs:31-34` |

**通道 3：ZIP mod** — `[PC源码:CSTI-ModLoader/ModLoader.cs:501-917]`

```csharp
var files = Directory.GetFiles(Path.Combine(Paths.BepInExRootPath, "plugins"));  // :505  ★ 不递归
if (!file.EndsWith(".zip")) continue;                                            // :508
ModDirName = entrys.ElementAt(0).FileName.Substring(0, len - 1);                 // :523  ★ 用"第一个条目"推顶层目录名
var ModInfoZip = zip[ModDirName + @"/ModInfo.json"];                             // :526
if (ModInfoZip == null) continue;                                                // :527-528
if (Info.ModEditorVersion.IsNullOrWhiteSpace()) continue;                        // :539-540  ★ 必须带 ModEditorVersion
```
内部结构与文件夹型一一对应：`Resource/*.ab` `:582`、`Resource/Picture/*` `:622`、`Resource/Audio/*` `:672`、`ScriptableObject/<Type>/*.json` `:732`、`Localization/*.csv` `:781`、`<Type>/*.json` `:809`、`GameSourceModify/**/*.json` `:887`。

**通道 4：预热（`Awake` 内，异步）** — `[PC源码:CSTI-ModLoader/LoaderUtil/LoadPreData.cs:93-176]`
同样只扫一层 `plugins` + 要求 `ModInfo.json` `[:101]`，做三件事：注册 `ModPacks`+配置项 `[:117-121]`、把 `Resource/Picture/**` 塞进异步图片队列 `[:138-146]`、把 `<TypeName>/**/*.json|*.jsonnet` 交给后台 Task 读字节 `[:156-157]`。
`ResourceLoadHelper.LoadUniqueObjs` 的枚举规则 `[:52-70]`：类型集合 = `GameSourceAssembly.GetTypes()` 里所有 `UniqueIDScriptable` 子类；对每个类型，若存在同名目录就**递归**收 `*.json` 和 `*.jsonnet`；若 `ModInfo.ModEditorVersion` 为空则只警告不读 `[:59-62]`。

**元数据从哪读**：`ModInfo` 类定义 `[PC源码:CSTI-ModLoader/ModLoader.cs:32-43]`，字段 `Name / Version / ModLoaderVerison / ModEditorVersion`。
读取方式一律 **Unity 原生 `JsonUtility.FromJsonOverwrite`**：文件夹型 `[:941]`、zip 型 `[:536]`、预热型 `LoadPreData.cs:111`；归档型的 mod 名直接是二进制里的一个字符串 `LoadArchMod.cs:71`。
`ModInfo` 里**没有** `Author` 字段（Windy 的 `ModInfo.json` 写了 `"Author": ""`）→ 未知键被 `JsonUtility` 静默忽略。`[实测:plugins2\Windy\ModInfo.json]`

**mod 名的作用域**：`ModName` 默认取文件夹名，若 `Info.Name` 非空则用它 `[:945-946]`；随后用于
① 配置项分节名 `是否加载某个模组` `[:950-952]`（这就是 `Dop.plugin.CSTI.ModLoader.cfg` 里那一长串的来源 `[实测:config\Dop.plugin.CSTI.ModLoader.cfg]`）；
② 新建对象的 `name` 前缀 `"{ModName}_{CardName}"` `[:847]`。
**配置项按 mod 名去重**：`if (!ModPacks.ContainsKey(ModName))` 才 Bind，已存在的只把 `Loaded` 置 true `[:948-956]`。

---

### 3. 解析（JSON → 对象；`XxxWarpData`/`XxxWarpType` 归谁管）

**JSON 库 = LitJson**（不是 Newtonsoft/Json.NET）：`using LitJson;` `[PC源码:CSTI-ModLoader/ModLoader.cs:19]`，`JsonMapper.ToObject(...)` `[:900, 1103, 1264]`，产出 `JsonData`。

**统一抽象 = `KVProvider`** `[PC源码:CSTI-ModLoader/ExportUtil/KVProvider.cs]`
- `JsonKVProvider`：包一层 **LitJson** `JsonData`（文件夹/zip 型走这条）
- `MapperObject` / `MapperList` / `ObjInt` / `ObjString`… `[PC源码:CSTI-ModLoader/ExportUtil/MapperObject.cs]`：`.modArch_V3` 专用的"字符串表 + 类型化节点"，键是 int 索引，靠 `StringMapper` 还原 `[MapperObject.cs:163-172]`

**两种填值路径**（关键区分）：

| 路径 | 使用者 | 代码 |
|---|---|---|
| **整对象一次性反序列化** | 新建对象的**全部普通字段**（含标量、字符串、内联结构、数组） | 若类型实现 `IModLoaderJsonObj` → `modLoaderJsonObj.CreateByJson(json)`；否则 `JsonUtility.FromJsonOverwrite(json, obj)`。`[ModLoader.cs:1104-1111, 838-845]` `[LoadArchMod.cs:302-309, 347-354, 454-461, 505-512]` `[LoadPreData.cs:49-56]` |
| **`JsonCommonWarpper` 增量 warp** | 只处理 `*WarpType`/`*WarpData` 成对键 + 下潜对象/数组 | `WarpperFunction.cs:119-465` |

**谁遍历 `XxxWarpData` + `XxxWarpType`：`WarpperFunction.JsonCommonWarpper`** `[PC源码:CSTI-ModLoader/WarpperFunction.cs:124-465]`

```csharp
foreach (var key in json.Keys) {                       // :142
    var keyData = json[key];
    if (key.EndsWith("WarpType")) {                    // :147
        if (!keyData.IsInt ||
            !json.ContainsKey(key.Substring(0, key.Length - 8) + "WarpData")) continue;   // :149
        ...                                            // 按 (int)keyData 分派
    }
    else if (key.EndsWith("WarpData")) continue;       // :386-387  孤立的 WarpData 直接跳
    else { /* 只有 IsObject / IsArray(元素是对象) 才下潜递归 */ }   // :388-458
}
```

调用者（4 处 + 自身递归）：

| 调用点 | 代码 |
|---|---|
| 新建的非 GUID `ScriptableObject` | `ModLoader.cs:1296`（`LoadEditorScriptableObject`） |
| 新建的 `UniqueIDScriptable`（含 CardData） | `DoWarpperLoader.cs:180`（`WarpperAllEditorMods`） |
| `GameSourceModify` 目标 | `DoWarpperLoader.cs:141`（`WarpperAllEditorGameSrouces`） |
| `MatchTag` 批量目标 | `DoWarpperLoader.cs:81`（`MatchAndWarpperAllEditorGameSrouce`） |
| 递归：ADD 新建的子对象 | `WarpperFunction.cs:243, 274` |
| 递归：MODIFY 命中的子对象 | `WarpperFunction.cs:306, 320, 359, 397, 419, 451` |

**字段怎么定位**：`WarpHelper.FieldFromCache` `[PC源码:CSTI-ModLoader/WarpHelper.cs:61-130]`
- `AccessTools.Field(type, field_name)`（`:75, :81`）→ **按名字精确匹配，大小写敏感**
- 结果缓存进 `FieldInfoCache`；getter/setter 用 DynamicMethod/Emit 生成；若源与字段都是引用类型，更进一步用 `UnsafeUtility.GetFieldOffset` + 指针直接读写（`AccessHelper`，`:184-253`）——这是 PC 端性能优化，也是它敢在几十秒里处理几十万次写入的原因。
- **找不到字段时 `fieldInfo == null`** → 后续 setter 为 null → `ObjectReferenceWarpper` 里被 catch 成一条 warning `[:484-488]`，**静默失败**。这正是"数据写错但日志不炸"的来源。

各 `WarpType` 具体落地见 **③ 表**。

---

### 4. 建对象（自定义 `CardData` 怎么创建）

**没有模板克隆，没有游戏工厂，就是 `ScriptableObject.CreateInstance(type)` + 反序列化。**

| 场景 | 代码 |
|---|---|
| 新建 `UniqueIDScriptable`（`.modArch_V3`） | `var card = ScriptableObject.CreateInstance(type) as UniqueIDScriptable;` `[PC源码:CSTI-ModLoader/ExportUtil/LoadArchMod.cs:501]` |
| 新建 `UniqueIDScriptable`（zip） | `var card = (UniqueIDScriptable)ScriptableObject.CreateInstance(type);` `[PC源码:CSTI-ModLoader/ModLoader.cs:835]` |
| 新建 `UniqueIDScriptable`（文件夹，预热后） | `var card = ScriptableObject.CreateInstance(type) as UniqueIDScriptable;` `[PC源码:CSTI-ModLoader/LoaderUtil/LoadPreData.cs:47]` |
| 新建非 GUID 的 `ScriptableObject` | `var obj = ScriptableObject.CreateInstance(type);` `[ModLoader.cs:1096]` / `[LoadArchMod.cs:298, 449]` |
| `WarpType.ADD` 里嵌套的新元素 | `sub_field_type.IsSubclassOf(typeof(ScriptableObject)) ? ScriptableObject.CreateInstance(sub_field_type) : sub_field_type.ConstructorFromCache()()` `[WarpperFunction.cs:230-232, 261-263]` |

- `type` 从哪来：`GameSourceAssembly.GetTypes()` 里按 **类型名** 匹配目录名/归档里的 `listStr[0]`（`ModLoader.cs:802-804`、`LoadArchMod.cs:487-488`、`ResourceLoadHelper.cs:52-54`）。
- 非 `UniqueIDScriptable` 的 `ScriptableObject` 用 `AccessTools.AllTypes()`（`ModLoader.cs:1069-1071`）而不是游戏程序集——这点与上面不同。`[PC源码:ModLoader.cs:1069]`
- 创建后立刻反序列化：`IModLoaderJsonObj.CreateByJson(json)` 优先，否则 `JsonUtility.FromJsonOverwrite(json, card)`。
- **命名规则**：`UniqueIDScriptable` → `card.name = $"{ModName}_{CardName}"`（`ModLoader.cs:847`、`LoadArchMod.cs:356, 520`、`LoadPreData.cs:58`）；非 GUID 对象 → `obj.name = obj_name`（= 文件名去扩展名，`ModLoader.cs:1102`、`LoadArchMod.cs:300, 451`）。
- **GUID 必须有**：建卡前会检查 `json.ContainsKey("UniqueID") && json["UniqueID"].IsString && 非空白`，否则 `LogErrorFormat("... try to load a UniqueIDScriptable without GUID")` 并 `continue`（`ModLoader.cs:826-833`、`LoadArchMod.cs:336-343, 492-499`、`LoadPreData.cs:38-45`）。
- `ScriptableObject.CreateInstance` 之后**没有**额外的 `Init()` 调用；`Init()` 由游戏循环负责（见 ②-1）。

---

### 5. 引用解析（PC 的真实规则）

规则**唯一入口**：`WarpperFunction.JsonCommonRefWarpper`（两个重载：标量 `:27-63`，`List<string>` `:65-117`）。
判据是 **目标字段的声明类型 `field_type`**，然后去一张固定的 static 字典里按键取值。

| 字段声明类型 | 查哪张表 | 键是什么 | 代码 |
|---|---|---|---|
| `UniqueIDScriptable` 或其子类 | `ModLoader.AllGUIDDict` | **`UniqueID` 字符串（32 位十六进制 GUID）** | `WarpperFunction.cs:30-33, 68-74` |
| 其它 `ScriptableObject` 子类 | `AllScriptableObjectWithoutGuidTypeDict[field_type]` | **对象名 `name`**（= mod 里那个 json 文件名 / 归档里的条目名） | `:34-40, 75-86` |
| `Sprite` | `ModLoader.SpriteDict` | **`Sprite.name`**（= 图片文件名去扩展名） | `:41-45, 87-93` |
| `AudioClip` | `ModLoader.AudioClipDict` | **`AudioClip.name`**（= 音频文件名去扩展名） | `:46-49, 94-100` |
| `WeatherSpecialEffect` 或其子类 | `ModLoader.WeatherSpecialEffectDict` | 对象名 | `:50-54, 101-105` |
| **恰好是** `ScriptableObject`（基类字段） | `ModLoader.AllScriptableObjectDict` | `UniqueIDScriptable` 用 **GUID**，其它用 **name** | `:55-58, 106-112` |
| 其它任何类型 | — | `LogErrorWithModInfo("JsonCommonRefWarpper Unexpect Object Type")` | `:61, 115` |

**这些表怎么建：`LoadGameResource()`** `[PC源码:CSTI-ModLoader/ModLoader.cs:386-499]`，在 ClearDict 前缀里第一个跑：

```csharp
foreach (var ele in Resources.FindObjectsOfTypeAll(typeof(ScriptableObject))) {   // :401
    if (ele.GetType().Assembly != GameSourceAssembly) continue;                   // :403 只收游戏自己的
    if (ele is UniqueIDScriptable s)  AllScriptableObjectDict[s.UniqueID] = s;    // :410-411
    else                              AllScriptableObjectDict[ele.name]  = ele;    // :418-421
    if (ele is not UniqueIDScriptable) AllScriptableObjectWithoutGuidTypeDict[type][ele.name] = ele;  // :430-443
    if (ele is UniqueIDScriptable id) {
        AllGUIDTypeDict[id.GetType()][id.name] = id;                              // :448-458
        AllGUIDDict[id.UniqueID] = id;                                            // :460-461
    }
}
foreach (var ele in Resources.FindObjectsOfTypeAll(typeof(Sprite)))       SpriteDict[ele.name] = ele;              // :473-480
foreach (var ele in Resources.FindObjectsOfTypeAll(typeof(AudioClip)))    AudioClipDict[ele.name] = ele;           // :482-489
foreach (var ele in Resources.FindObjectsOfTypeAll(typeof(WeatherSpecialEffect))) WeatherSpecialEffectDict[ele.name] = ele;  // :491-498
```

**mod 自己的资源怎么进表**：
- `Resource/*.ab` → `ab.LoadAllAssets()`，`Sprite`/`AudioClip` 按 **asset.name** 入 `SpriteDict`/`AudioClipDict` `[ModLoader.cs:983-1003]`
- `Resource/Picture/*.{png,jpg,jpeg}` → `Sprite.Create(...)`，`sprite.name = 文件名去扩展名` `[ModLoader.cs:651-655]`；**异步版**在 `PostSpriteLoad.cs:158-163`
- `Resource/Audio/*.{wav,mp3,ogg}` → `ResourceDataLoader.GetAudioClipFrom*`，`clip.name = 文件名去扩展名` `[ModLoader.cs:1019-1054]`
- 归档里的图：`ImgBLK` 用归档里的名字 `Path.GetFileNameWithoutExtension(ImgName)` 当键 `[LoadArchMod.cs:158, 603]`

**三个必须知道的细节**：

1. **`Sprite` 有专用延迟通道**。当字段类型是 `Sprite` 且 `WarpData` 是**字符串**时，`JsonCommonRefWarpper` **不查表**，而是把 setter 入队：
   `obj.PostSetEnQueue(setter, data);` `[PC源码:CSTI-ModLoader/WarpperFunction.cs:41-45]` → `PostSpriteLoad.PostSetEnQueue` `[PostSpriteLoad.cs:65-75]` → 贴图真正解码出来、`SpriteDict.Add` 成功后按 ID 回放 `Set()` `[PostSpriteLoad.cs:56-62, 161-172]`。
   这就是为什么"图片是延迟加载的，刚进游戏有些卡缺图"（更新历史 v2.1.4）——**不是数据错，是引用被排队了**。
2. **字典键的大小写**：`JsonData.ContainsKey` 是 `Dictionary<string,JsonData>` 的精确查找；`MapperObject.ContainsKey` 走 `StringMapper.GetIndex(key)` `[ExportUtil/MapperObject.cs:163-168]`。→ **键名必须与 C# 成员名逐字符一致。**
3. **PC 端没有"按名字兜底查 GUID"这回事**。名字只在"非 UniqueID 的 ScriptableObject / Sprite / AudioClip / WeatherSpecialEffect"这四类桶里当键。`UniqueIDScriptable` 的引用**只认 GUID**。

---

### 6. 注册（写回哪些表、什么顺序）

**写回的表**（全部是 `ModLoader` 的 static 字段，声明在 `[PC源码:CSTI-ModLoader/ModLoader.cs:86-178]`）：

| 表 | 内容 | 写入点 |
|---|---|---|
| `AllGUIDDict` | `GUID → UniqueIDScriptable` | `ModLoader.cs:851`、`LoadArchMod.cs:362, 526`、`LoadPreData.cs:62` |
| `AllGUIDTypeDict` | `Type → (GUID → obj)` | `ModLoader.cs:864-866`、`LoadArchMod.cs:376-378, 540-542`、`LoadPreData.cs:74-76`。⚠️ **只在桶已存在时才写**（`if (AllGUIDTypeDict.TryGetValue(type, out var dict))`），桶由 `LoadGameResource` 按原版对象建立 `[ModLoader.cs:448-458]` |
| `AllScriptableObjectDict` | `GUID 或 name → ScriptableObject` | `ModLoader.cs:862-863, 1117-1118`、`LoadArchMod.cs:315-316, 374-375, 538-539` |
| `AllScriptableObjectWithoutGuidTypeDict` | `Type → (name → obj)` | `ModLoader.cs:1113`、`LoadArchMod.cs:311-312, 469-470` |
| **`GameLoad.Instance.DataBase.AllData`** | **游戏主数据表** ★最关键 | `ModLoader.cs:852`、`LoadArchMod.cs:363, 527`、`LoadPreData.cs:63` |
| `SpriteDict` / `AudioClipDict` / `WeatherSpecialEffectDict` | 资源 | 见 ②-5 |
| `UniqueIdObjectExtraData` / `ScriptableObjectExtraData` / `ClassObjectExtraData` | JSON 里 `"额外数据ExtraData"` 键的内容 `[WarpperFunction.cs:129-140, 467]` | 同上 |
| `ModPacks` + BepInEx `ConfigEntry<bool>` | mod 开关（`是否加载某个模组/<ModName>_<Name>`） | `ModLoader.cs:948-952`、`LoadPreData.cs:117-121`、`LoadArchMod.cs:82-84` |
| 各类**延迟清单**（见下） | 等对应 Manager 初始化时才生效 | `ModLoader.cs:153-178` |

**顺序与时序**（`UniqueIDScriptableClearDictPrefix` 全文 `[PC源码:CSTI-ModLoader/ModLoader.cs:1747-1802]`，严格顺序）：

| # | 步骤 | 行 | 写回什么 |
|---|---|---|---|
| 0 | `if (!_once.DoOnce()) return;` | `:1749` | 幂等（`SimpleOnce` 见 `ResourceLoadHelper.cs:115-133`） |
| 1 | `LoadGameResource()` | `:1760` | 收编全部原版 SO/资源到 6 张表 |
| 2 | `LoadArchMod.LoadAllArchMod()` | `:1762` | 建卡 → `AllGUIDDict` + `AllData`；GSM → `WaitForWarpperEditorGameSourceGUIDList` |
| 3 | `LoadMods(<root>\plugins)` | `:1764` | 建非 GUID SO + 资源；GSM；CSV |
| 4 | `LoadModsFromZip()` | `:1766` | 同上（zip 源） |
| 5 | `PostSpriteLoad.BeginCompress = true` | `:1767` | 启动贴图解码协程 |
| 6 | `LoadPreData.LoadFromPreLoadData()` | `:1769` | **建 `UniqueIDScriptable`（含 CardData）** → `AllGUIDDict` + `AllData` → `WaitForWarpperEditorGuidDict` |
| 7 | `LoadEditorScriptableObject()` | `:1771` | 消化 `WaitForWarpperEditorNoGuidList` → `JsonCommonWarpper`；分流出 `WaitForAddCardTabGroup` / `WaitForAdd{Default,Main}ContentPage` / `WaitForAddGuideEntry` |
| 8 | `DoWarpperLoader.WarpperAllEditorMods()` | `:1777` | warp 新建 mod 对象；分流 `WaitForAddBlueprintCard` / `WaitForAddCardFilterGroupCard` / `WaitForAddPerkGroup` / `WaitForAddVisibleGameStat` / `WaitForAddJournalPlayerCharacter`；`CardTabGroup.IncludedCards.Add` |
| 9 | `DoWarpperLoader.WarpperAllEditorGameSrouces()` | `:1779` | **GameSourceModify**：按 GUID 找目标（先 `AllGUIDDict`，`obj==null` 时在 `:110-113` 再查一次），`ModLoaderSpecialOverwrite` 处理，`JsonCommonWarpper`，`FillDropsList()` |
| 10 | `DoWarpperLoader.MatchAndWarpperAllEditorGameSrouce()` | `:1781` | 先按 `CardData.CardTags` 建 `AllCardTagGuidCardDataDict` `[:25-45]`，再处理 `MatchTagWarpData` ∩ 交集 + `MatchTypeWarpData` `[:56-88]` |
| 11 | `AddPerkGroup()` | `:1786` | `Array.Resize(ref PerkGroup.PerksList, +1)` 追加 `CharacterPerk` `[:1415-1429]` |
| — | `finally { PostSpriteLoad.CanEnd = true; }` | `:1800` | 允许贴图协程收尾 |
| 之后 | 游戏 `for AllData → Init()` | `[PC游戏IL]` | 每个对象 `RegisterID()` → `AllUniqueObjects` |

**"延迟清单"是注册的第二波**（warp 阶段只登记，真正生效要等对应 Manager）：

| 清单 | 何时真正写进游戏 | 代码 |
|---|---|---|
| `WaitForAddPerkGroup` | `AddPerkGroup()`，ClearDict 前缀内 | `ModLoader.cs:1415-1429, 1786` |
| `WaitForAddBlueprintCard` | `GraphicsManager.Init` postfix | `ModLoader.cs:1367-1390, 1838` |
| `WaitForAddVisibleGameStat` | `GraphicsManager.Init` postfix | `ModLoader.cs:1392-1413, 1840` |
| `WaitForAddCardTabGroup` | `GraphicsManager.Init` postfix（`AddCardTabGroup`） | `ModLoader.cs:1437-1463, 1836` |
| `WaitForAddCardFilterGroupCard` | `GraphicsManager.Init` postfix（仅首次，`init_flag`） | `ModLoader.cs:1498-1514, 1848` |
| `WaitForAddJournalPlayerCharacter` / `WaitForAddGuideEntry` | `GuideManager.Start` prefix | `ModLoader.cs:1719-1744, 1816-1828` |
| `WaitForAdd{Default,Main}ContentPage` | `GraphicsManager.Init` postfix（`CustomGameObjectFixed`/`WaiterForContentDisplayer`） | `ModLoader.cs:1546-1698, 1846` |
| `WaitForLoadCSVList` | `LocalizationManager.LoadLanguage` postfix | `ModLoader.cs:1321-1365, 1804-1814` |

---

### 7. 事件与交互：**"数据写对就生效"成立吗？**

先给最关键的结论，再给证据。

#### 7.1 PC 端是否给"交互判定"打过补丁？

**打了，但不在 JSON loader 自己身上，而且对纯数据 mod 是直通 no-op。**

**(a) `ModLoader` 自己的 Harmony 补丁只有 5 个**（全文 grep `HarmonyInstance.Patch` 的全部命中）：

```
[PC源码:CSTI-ModLoader/ModLoader.cs:280]  UniqueIDScriptable.ClearDict       (prefix)
[PC源码:CSTI-ModLoader/ModLoader.cs:294]  LocalizationManager.LoadLanguage   (postfix)
[PC源码:CSTI-ModLoader/ModLoader.cs:308]  GuideManager.Start                 (prefix)
[PC源码:CSTI-ModLoader/ModLoader.cs:323]  GraphicsManager.Init               (postfix)
[PC源码:CSTI-ModLoader/ModLoader.cs:334]  FXMask.Awake
```
**没有一个是交互判定方法。**

**(b) 但它会拉起两个随包发布的辅助程序集**（这也是那条 `[BepInDependency]` 存在的原因）：

```
[PC源码:CSTI-ModLoader/ModLoader.cs:76]   NormalPatcher.DoPatch(HarmonyInstance)   ← CSTI_LuaActionSupport.dll
[PC源码:CSTI-ModLoader/ModLoader.cs:253]  MainPatcher.DoPatch(HarmonyInstance)     ← CSTI-ChatTreeLoader.dll
```

用 Mono.Cecil 扫 `CSTI_LuaActionSupport.dll` 的 `[HarmonyPatch]` 特性，它确实覆盖了交互判定：

```
CardActionPatcher.LuaCardActionQuickRequirementsCheck      → CardAction::CardsAndTagsAreCorrect
CardActionPatcher.LuaCardOnCardActionCardsAndTagsAreCorrect→ CardOnCardAction::CardsAndTagsAreCorrect
CardActionPatcher.LuaActionWillHaveAnEffect                → CardAction::WillHaveAnEffect
CardActionPatcher.LuaDismantleActionButton_Setup           → DismantleActionButton::Setup
CardActionPatcher.LuaCardAction / LuaCardOnCardAction      → GameManager::ActionRoutine / CardOnCardActionRoutine
OnGameLoad.DoOnAfterModLoader                              → UniqueIDScriptable::ClearDict
UITools.CardSlot_OnDrop / OnBeginDrag / OnDrag / OnEndDrag / InGameCardBase_OnPointerClick / DropInInventory
ObjModifyPatcher.DismantleActionButton_PostSetup           → DismantleActionButton::Setup
```
`[实测:Cecil 扫 F:\...\plugins2\CSTI-ModLoader\ModLoader\CSTI_LuaActionSupport.dll 的 HarmonyPatch 特性]`

**(c) 但这些补丁是"Lua 钩子闸门"，纯数据 mod 走不到里面。**

反编译 `CSTI_LuaActionSupport.AllPatcher.CardActionPatcher.LuaCardActionQuickRequirementsCheck`：

```
IL_0000: ldarg.0
IL_0001: ldflda LocalizedString CardAction::ActionName
IL_0006: ldfld  System.String LocalizedString::LocalizationKey
IL_001A: ldstr "CardActionPack"
IL_001F: call   System.String::StartsWith
IL_002A..IL_003A: 取 Nullable<bool>；无值或为 false
IL_0065: ret                       ← ★ 直接返回，什么都不做
IL_003C: ... CardActionPack::GetActionPack(ParentObjectID) ...
```
`LuaActionWillHaveAnEffect` 同形（`StartsWith("LuaCardAction")` / `"LuaCardOnCardAction"`）。

→ **判据是 `ActionName.LocalizationKey` 是否以 `CardActionPack` / `LuaCardAction` / `LuaCardOnCardAction` 等保留前缀开头。** Windy 这类纯数据 mod 的 `LocalizationKey` 形如 `Windy_Windy_CardDescription` `[实测:plugins2\Windy\CardData\Windy.json]`，**不匹配 → 补丁立刻 `ret`**。

**所以：对纯数据 mod，PC 端的交互判定路径与"没装 ModLoader"完全一致，游戏直接读 `CardData` 上的字段。**

#### 7.2 `CardInteractions` / `DismantleActions` / `ExplorationResults` / `TimeOfDayMods` / `SpoilageTime` 是纯数据吗？

**分两种情况：**

**情况 A — 新建的 mod 卡（`CardData/xxx.json`）：是纯数据。**
这些字段都是 `CardData` 上的普通序列化字段，`JsonUtility.FromJsonOverwrite(json, card)` 一次写全（`ModLoader.cs:844` / `LoadArchMod.cs:511` / `LoadPreData.cs:55`）。
**内层引用**（例如 `CardInteractions[i].CompatibleCards.TriggerCardsWarpData + TriggerCardsWarpType = 3`）由 `JsonCommonWarpper` 的**递归下潜**处理：
- 键是对象且字段不是 `UnityEngine.Object` → 递归 `[WarpperFunction.cs:390-399]`
- 键是对象数组 → 逐个元素递归（`List<>` `:409-422`，数组 `:423-454`）
- 遇到 `*WarpType` 就走 ③ 表的分派 `[:147-385]`

**情况 B — 改造原版卡（`GameSourceModify/<GUID>.json`）：不是纯数据，有 3 个额外前提。**

1. **标量必须包在 `MODIFY(5)` 里，否则被忽略。**
   `JsonCommonWarpper` 对普通标量键**没有任何分支**（`:388-458` 只处理 `IsObject` / `IsArray`）。
   例外：若 JSON 里带 `"ModLoaderSpecialOverwrite": true`，则先整体 `JsonUtility.FromJsonOverwrite(json, obj)` `[DoWarpperLoader.cs:128-139]`，示例见 `[PC源码:CSTI-ModLoader/ExampleModify.json]`（`{"BaseRatePerTick": -0.5, "ModLoaderSpecialOverwrite": true}`）。
   实测 Windy 的 GSM 全部采用 warp 对写法，顶层就是 `"DismantleActionsWarpType": 5` + `"DismantleActionsWarpData": [...]` `[实测:plugins2\Windy\GameSourceModify\windy\05f307ca...json]`。
   Windy GSM 里 WarpType 出现次数：**3 → 801，4 → 85，5 → 21，6 → 3**（无 1/2）`[实测:统计 plugins2\Windy\GameSourceModify\**\*.json]`。

2. **`CardData.FillDropsList()` 必须改完重跑。**
   `FillDropsList` 是 **private**（`[PC游戏IL:CardData::FillDropsList]` → `IsPublic=False, IsPrivate=True`），游戏只在 `CardData.Init()` 里调一次。
   GSM 改的是**已经初始化过**的原版卡，`Init()` 早跑完了 → 若不重跑，掉落表还是旧的。
   PC 的做法：每次 warp 完一个 `CardData` 立刻
   ```csharp
   Traverse.Create(cardData).Method("FillDropsList")?.GetValue();
   ```
   `[PC源码:CSTI-ModLoader/LoaderUtil/DoWarpperLoader.cs:82, 146, 210]`
   **这是"数据写对也不一定生效"的最硬实证。**

3. **`MatchTag` 型 GSM 走的是完全不同的"批量匹配"路径**：先遍历 `AllGUIDDict` 用 `CardData.CardTags` 建 `AllCardTagGuidCardDataDict` `[DoWarpperLoader.cs:25-45]`，再对 `MatchTagWarpData` 求交集、可用 `MatchTypeWarpData` 过滤 `CardType` `[:56-88]`，命中的每张卡都 warp 一遍**并各跑一次 `FillDropsList()`** `[:82]`。（更新历史 v1.2.3）

#### 7.3 还有哪些"不是写字段就完事"的步骤

| 类别 | 需求 | 代码 |
|---|---|---|
| **UI 索引** | 卡牌分类页 / 蓝图列表 / 统计页 / 过滤器 / 日志角色 / 引导条目 / 内容页 | `GraphicsManager.Init` postfix + `GuideManager.Start` prefix 里的 6+2 个 `Add*`（见 ②-6 表），**全部延迟到对应 Manager 初始化时** |
| **perk 分组** | `CharacterPerk` 要进 `PerkGroup.PerksList`，JSON 字段名 `CharacterPerkPerkGroup` **不是游戏字段**，是给 loader 的指令 | `DoWarpperLoader.cs:217-223` → `AddPerkGroup()` `ModLoader.cs:1415-1429`（`Array.Resize` + 尾插） |
| **自定义角色** | `PlayerCharacter` 要进**每个** `Gamemode.PlayableCharacters` | `DoWarpperLoader.cs:232-248`（`Array.Resize` + 尾插） |
| **蓝图卡分组** | `BlueprintCardDataCardTabGroup` / `...SubGroup` → `group.ShopSortingList.Add` + `sub_group.IncludedCards.Add` | `DoWarpperLoader.cs:183-192` → `AddBlueprintCardData` `ModLoader.cs:1367-1390` |
| **物品页签** | `ItemCardDataCardTabGpGroup` → `CardTabGroup.IncludedCards.Add` | `DoWarpperLoader.cs:194-201` |
| **过滤器** | `CardDataCardFilterGroup`（水/食物/工具/火） | `DoWarpperLoader.cs:203-208` → `AddCardFilterGroupOnce` `ModLoader.cs:1498-1514` |
| **贴图/卡面** | 图片是延迟异步加载的；引用先入 `PostSetQueue` 排队；队列排空后**全量刷新卡面** | `PostSpriteLoad.cs:65-75, 95-222`；收尾处 `foreach (var graphics in Resources.FindObjectsOfTypeAll<CardGraphics>()) graphics.Setup(graphics.CardLogic);` `[:192-204]`（更新历史 v2.1.5"现在会自动更新卡面了"） |
| **本地化** | CSV 只在**当前语言**是简体中文/English 时按文件名含 `SimpCn`/`SimpEn` 应用，且**不覆盖已有键** | `ModLoader.cs:1324-1364`（`if (!CurrentTexts.ContainsKey(...))`） |
| **脚本对象额外数据** | `"额外数据ExtraData"` 存起来给 Lua/对话树用，不写进游戏对象 | `WarpperFunction.cs:129-140` |

#### 7.4 结论（针对本任务的核心问题）

**"数据写对就该生效" 这个前提，对"新建 mod 卡 + 纯数据字段"基本成立；对 GSM 改造原版卡、以及任何依赖索引/贴图/本地化的内容，不成立。**

**PC 也要额外步骤，点名如下 5 项：**
1. `CardData.FillDropsList()` 重跑（掉落/产出类字段改完必须重跑，否则旧值） — `DoWarpperLoader.cs:82, 146, 210`
2. `CharacterPerk → PerkGroup.PerksList` 的数组扩容追加（JSON 里的 `*PerkGroup` 字段是 loader 指令而非游戏字段） — `ModLoader.cs:1415-1429`
3. `PlayerCharacter → Gamemode.PlayableCharacters` 扩容追加 — `DoWarpperLoader.cs:232-248`
4. 六类 UI/索引注册必须等 `GraphicsManager.Init` / `GuideManager.Start` 钩子 — `ModLoader.cs:1832-1856, 1816-1828`
5. 贴图是**延迟**加载：Sprite 引用先入 `PostSetQueue`，队列排空后还要 `CardGraphics.Setup()` 全量刷新卡面 — `PostSpriteLoad.cs:65-75, 192-204`

**另外两个"隐性前提"（不满足则静默失败）：**
6. 每个 `UniqueIDScriptable` 必须带非空 `UniqueID`，否则被跳过并打 `"... without GUID"` 错误。`[ModLoader.cs:826-833]`
7. 引用键必须逐字符正确且目标已注册；查不到**不报错**。`[WarpperFunction.cs:477-489, 507-508]`

**测量数据（性能参考）**：一台满配 PC 在 2024-02-18 的真实日志里 `warp time taken:00:00:47.1600422`——**纯粹 warp 阶段 47 秒** `[实测:F:\...\BepInEx\LogOutput.log.1]`（该日志来自 `D:\Program Files (x86)\Steam\...` 的旧安装，非当前 F: 盘安装）。这解释了 Android 端为什么必须重写 warp 通路而不是照搬。

---

### 8. 与移动端差异（`D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\`）

> 说明：仓库 `android-06-port` 分支里的 `CSTI-MiniLoader/`（提交 `fb96a5f` 标注"最终宣告失败"）是**早期尝试**；`ml-installer-06\mods-06\CSTI-MiniLoader-06\` 是**现行工作副本**（多出 `DragProbe.cs` 等，仓库那份没有）。本节以**现行工作副本**为准。

逐条差异见 **⑤**。这里先给三个"结构性"判断：

- **架构上，移动端已经对齐了 PC 的 0–11 步骨架**（`LoadPatchMain.LoadAndInit` 的 `[STEP] 1..9` 与 PC 的 1..11 一一对应），这是好消息。
- **最大的结构性偏离是"触发方式"**：PC 用 `ClearDict` 前缀这一个"硬钩子"精确卡点，移动端用**轮询 + 自己调 `Init()`**。这决定了后面一堆差异（尤其是 ④⑤⑪）。
- **移动端只支持 `.modArch_V3` 一条发现通道**，PC 的"文件夹型 mod"和"zip mod"两条都没有。Windy 的原始形态是**文件夹**，所以在移动端必须先打包。

---

## ③ `WarpType` 语义表（PC 2.3.6.35 真实实现）

枚举定义 `[PC源码:CSTI-ModLoader/WarpperFunction.cs:16-25]`：

```csharp
public enum WarpType { NONE, COPY, CUSTOM, REFERENCE, ADD, MODIFY, ADD_REFERENCE }
//                       0     1      2        3        4      5         6
```

**识别条件（统一）** `[WarpperFunction.cs:147-150]`：键名以 `WarpType` 结尾、值是 **int**、且**同一层**存在 `<去掉WarpType的前缀>WarpData`。
`WarpData` 的具体形态（字符串 / 数组 / 对象）决定走哪条分支。

| 值 | 名称 | 实现？ | 触发条件 | 具体落地 | 代码 |
|---|---|---|---|---|---|
| 0 | `NONE` | ❌ | — | 落到 `else` 分支 → `LogErrorWithModInfo("CommonWarpper Unexpect WarpType")` | `:381-384` |
| 1 | `COPY` | ❌ | — | 同上。**2.3.6.35 不实现**（v2.0.0 起"不再支持非 Editor 格式 JsonMod"） | `:381-384` |
| 2 | `CUSTOM` | ❌ | — | 同上 | `:381-384` |
| **3** | `REFERENCE` | ✅ | `WarpData` 是**字符串** | 按字段声明类型选表（见 ④），单键查表 → 直接 `setter(obj, found)`；查不到**静默无操作** | `:151-164` → `JsonCommonRefWarpper(string)` `:27-63` → `ObjectReferenceWarpper` `:469-490` |
| **3** | `REFERENCE` | ✅ | `WarpData` 是**数组** | 目标字段必须是 `List<>` 或数组（否则 `LogErrorWithModInfo("... REFERENCE Must be list or array ...")` `:179-181`）；取元素类型 `sub_field_type`（`List<>` 取泛型参 / 数组取元素类型 `:168-176`）；逐元素查表写回。<br>**数组**：`ArrayResize` 到 `data.Count` 后按 index 覆写 `:510-518`；**`List<>`**：逐个 `Add`（**追加**语义，不先清空）`:503-509` | `:165-204` → `:492-525` |
| **4** | `ADD` | ✅ | `WarpData` 是**数组**，元素是**对象** | 目标字段必须是 `List<>`/数组 `:284-288`。对每个元素：<br>① 新建实例 `sub_field_type.IsSubclassOf(ScriptableObject) ? ScriptableObject.CreateInstance(sub) : sub.ConstructorFromCache()()` `:230-232, 261-263`<br>② `IModLoaderJsonObj.CreateByJson` 或 `JsonUtility.FromJsonOverwrite` `:233-241, 264-272`<br>③ **递归** `JsonCommonWarpper(new_obj, elem)` `:243, 274`（所以嵌套 `*WarpData` 会被解析）<br>④ 追加：`List` → `instance.Add` `:244`；数组 → `ArrayResize(+count)` 后按 `start_idx` 写 `:255-282` | `:211-295` |
| **5** | `MODIFY` | ✅ | `WarpData` 是**对象** | 取**现有**子对象 `getter(obj)` → 递归 `JsonCommonWarpper` → `setter` 写回。**就地改，不替换对象** | `:303-308` |
| **5** | `MODIFY` | ✅ | `WarpData` 是**数组**（元素是对象） | 按 index `i` 就地改**现有**元素：`List` → `instance[i]` 改完写回 `:314-322`；数组 → `instance.GetValue(i)` 改完 `SetValue` `:330-367`。⚠️ `i` 超出原长度时 `GetValue` 会抛，catch 里打一行 `On access {id}::{type}.{field} : {e}` 的 warning `:340-357` | `:309-374` |
| **6** | `ADD_REFERENCE` | ✅ | `WarpData` 是字符串 | 单值：**报错**，`ObjectAddReferenceWarpper(string)` 只打 `"... Only Vaild in List or Array Filed"` `:527-532` | `:151-153` → `:527-532` |
| **6** | `ADD_REFERENCE` | ✅ | `WarpData` 是**数组** | 与 `REFERENCE` 的差别**只在数组字段**上体现：`List` 两者都是 `Add`；数组两者都是 `ArrayResize` 后写。语义上 `ADD_REFERENCE` 表达"追加"意图，代码走不同实现（`ObjectAddReferenceWarpper` vs `ObjectReferenceWarpper`） | `:70-73, 79-80, 89-90, 96-97, 108-109` → `:534-568` |
| 其它 int | — | — | — | `LogErrorWithModInfo("CommonWarpper Unexpect WarpType")` | `:381-384` |

**配套规则**：

| 规则 | 代码 |
|---|---|
| 孤立的 `*WarpData`（同层没有对应 `*WarpType`）→ 直接跳过 | `:386-387` |
| `*WarpType` 值不是 int → 跳过 | `:149` |
| `*WarpType` 有，但同层没有 `*WarpData` → 跳过 | `:149` |
| **普通标量键**（既非 `WarpType` 也非 `WarpData`，也不是对象/对象数组）→ **完全无视** | `:388-458` 无标量分支 |
| 普通**对象**键 → 递归下潜（字段是 `UnityEngine.Object` 则跳过） | `:390-399` |
| 普通**对象数组**键 → 逐元素递归（`List<>` 或数组；元素为 `null` 跳过） | `:400-457` |
| `"额外数据ExtraData"` → 存进 `UniqueIdObjectExtraData` / `ScriptableObjectExtraData` / `ClassObjectExtraData` | `:129-140`, `:467` |
| 每个键的处理都包在 try/catch 里，异常只打一行 warning（`LogErrorWithModInfo`） | `:460-463`, `:380-384` |

**实测使用分布（Windy 的 `GameSourceModify`）**：`3`=801、`4`=85、`5`=21、`6`=3，无 `1`/`2` `[实测]`。

---

## ④ 引用定位规则表（PC 真实规则）

| # | 字段声明类型 | 查哪张表 | **键** | 表的填充 | 代码 |
|---|---|---|---|---|---|
| 1 | `UniqueIDScriptable` 或其子类（`CardData`/`CardTag`/`GameStat`/`CharacterPerk`/`Objective`/`Encounter`/`PlayerCharacter`/`Gamemode`/`PerkGroup`/`SelfTriggeredAction`/…） | `ModLoader.AllGUIDDict` | **`UniqueID` 字符串 = 32 位十六进制 GUID**（GSM 的文件名就是它） | `LoadGameResource` `ModLoader.cs:460-461`；mod 新建 `ModLoader.cs:851` | `WarpperFunction.cs:30-33, 68-74` |
| 2 | 其它 `ScriptableObject` 子类（非 `UniqueIDScriptable`） | `AllScriptableObjectWithoutGuidTypeDict[字段类型]` | **对象 `name`**（= mod 里那个 `.json` 文件名去扩展名；归档里 = 条目名） | `ModLoader.cs:430-443`（原版）/ `:1113`、`LoadArchMod.cs:311-312`（mod） | `:34-40, 75-86` |
| 3 | `Sprite` | `ModLoader.SpriteDict` | **`Sprite.name`** = 图片文件名去扩展名（`.png/.jpg/.jpeg`，或 `.dds`，或 AssetBundle 内 asset 名） | `ModLoader.cs:473-480`（原版）/ `:651-655, 988-989`、`LoadArchMod.cs:158, 603`（mod） | `:41-45, 87-93` |
| 4 | `AudioClip` | `ModLoader.AudioClipDict` | **`AudioClip.name`** = 音频文件名去扩展名（`.wav/.mp3/.ogg`） | `ModLoader.cs:482-489` / `:997-998, 1019-1054`、`LoadArchMod.cs:646-678` | `:46-49, 94-100` |
| 5 | `WeatherSpecialEffect` 或其子类 | `ModLoader.WeatherSpecialEffectDict` | **对象 `name`** | `ModLoader.cs:491-498` | `:50-54, 101-105` |
| 6 | 声明类型**恰好是** `ScriptableObject`（基类） | `ModLoader.AllScriptableObjectDict` | `UniqueIDScriptable` → **GUID**；其它 → **name** | `ModLoader.cs:410-421` / `:862-863, 1117-1118` | `:55-58, 106-112` |
| 7 | 其它任何类型 | — | 报错 `"JsonCommonRefWarpper Unexpect Object Type <type>"` | — | `:61, 115` |

**特殊通道（覆盖规则 3）**：
> 当字段类型是 `Sprite` **且** `WarpData` 是**字符串**时，**不查表**，而是把 setter 入队到 `PostSpriteLoad.PostSetQueue`，等贴图异步解码完成后回放。
`[PC源码:CSTI-ModLoader/WarpperFunction.cs:41-45]` → `[PostSpriteLoad.cs:56-75, 158-172]`

**键的大小写**：`JsonData.ContainsKey` 精确匹配；`MapperObject.ContainsKey` 经 `StringMapper.GetIndex(key)` `[ExportUtil/MapperObject.cs:163-168]`。→ **逐字符一致，无忽略大小写、无归一化。**

**查不到时的行为**：`ObjectReferenceWarpper` 里 `if (dict.TryGetValue(data, out var ele)) { setter(...) }`——**查不到就什么都不做，不报错、不警告** `[WarpperFunction.cs:477-489, 507-508]`。这就是"数据里引用了不存在的 GUID，加载不报错但卡是空的"的原因。

**PC 没有的机制**：没有"按名字兜底查 GUID"，没有"忽略大小写/归一化"，没有"跨类型桶兜底"。**只有第 2/5/6 类（非 UniqueID 对象）才用名字。**

---

## ⑤ ★ 移动端差异清单（PC 有 / 我们缺 / 影响 / 建议）

**对照基线**：PC = `D:\RiderProjects\CSTI-ModLoader\CSTI-ModLoader\**`（= 上游 `2.3.6.35`）。
移动端 = `D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\**`。

| # | 维度 | PC（2.3.6.35） | 移动端（现行） | PC 有 / 我们缺 | 影响 | 建议 |
|---|---|---|---|---|---|---|
| 1 | 宿主框架 | BepInEx 5.4.21 + Mono，`BaseUnityPlugin` `[ModLoader.cs:65]` | MelonLoader 0.6 + .NET 8 + Il2CppInterop，`MelonMod` `[MiniLoader.cs:14]` | — | — | 无需处理 |
| 2 | 补丁安装 | `Awake()` 里显式装 5 个 patch `[ModLoader.cs:280,294,308,323,334]` | **默认一个都不装**：`SkipHarmonyPatchAll = true` `[MiniLoader.cs:205]`，走 `Pump.Install()` + `OnUpdate → HookFree.Tick()` `[MiniLoader.cs:406-415, 356-367]` | 机制不同 | 移动端"免 hook 注入"，规避 il2cpp Harmony 风险 | 保持。注意 `SkipHarmonyPatchAll=false` 分支（`HarmonyIns.PatchAll`）是**未充分验证**路径 |
| 3 | 触发时机 | **硬钩 `UniqueIDScriptable.ClearDict` 前缀** `[ModLoader.cs:280]` | 无此钩子（`[HarmonyPrefix,HarmonyPatch(typeof(UniqueIDScriptable), nameof(UniqueIDScriptable.ClearDict))]` 在 `[LoadPatchMain.cs:321]` **是注释掉的**）；改为轮询 `UniqueIDScriptable.AllUniqueObjects.Count > 0` `[HookFree.cs:60-76]` → `RunDeferredInit()` `[MiniLoader.cs:340-350]` | **PC 的精确卡点我们没有** | 移动端用"注册表非空"当就绪判据，是**启发式**；理论上存在"非空但未加载完"的窗口 | 保留轮询；若要更精确，可考虑在 il2cpp 侧找 `GameLoad.LoadMainGameData` 或等价方法的 hook 点 —— **需移动端 il2cpp 校核** |
| 4 | **`Init()` 谁调** | **loader 从不调**（grep `\.Init()` 在 PC 源码 0 命中）；靠游戏 `for AllData → Init()` `[PC游戏IL:GameLoad/<AwakeWithLoadingScreen>d__53 IL_0079-IL_00C0]` | **自己调**：`foreach (var (id, o) in AllGUIDDict) o.Init();` `[LoadPatchMain.cs:411-415]` | **PC 有游戏兜底，我们没有** | `Init()` = `RegisterID()`（进 `AllUniqueObjects`）+ `CardData.FillDropsList()` + `CachedFilter.Clear()`。移动端**只对 `AllGUIDDict` 里的对象**调，覆盖面比 PC 的"整个 `AllData`"窄 | ⚠️ **需移动端 il2cpp 校核**：① 游戏的 `ClearDict`/`Init` 循环在 il2cpp 版是否仍存在；② 移动端 append 进 `DataBase.AllData` 的对象是否也会被游戏循环到（若会，重复调用安全，`RegisterID` 第二次只记 `Duplicates`）；③ `FillDropsList` 是否被覆盖到 —— 若没有，这正是差异 11 的风险点 |
| 5 | 发现路径 · 数量 | **4 条**：`Paths.PluginPath` 递归 `*.modArch_V3/V2/modArch` `[ExportUtil/LoadArchMod.cs:20-63]`；`plugins` 一层子目录 + `ModInfo.json` `[ModLoader.cs:926-930]`；`plugins/*.zip` `[ModLoader.cs:505-508]`；预热异步通道 `[LoadPreData.cs:93-176]` | **1 条**：`Directory.EnumerateFiles(MelonEnvironment.ModsDirectory, "*.modArch_V3", AllDirectories)` `[LoadUtil/LoadArchMod.cs:175-195]` | **PC 有文件夹型 mod、有 zip、有 V1/V2 归档；我们都没有** | Windy / Cod 这类 mod 的**原始形态是文件夹**，直接丢进移动端**不会被发现** | 用仓库已有的 `CSTI-MiniLoader/tools/archpack.py` 把文件夹打成 `.modArch_V3`；若希望原生支持文件夹 mod，需要移植 `LoadMods` + `LoadPreData.LoadData` |
| 6 | `ModInfo.json` 要求 | 文件夹型/zip 型必需 `[ModLoader.cs:930, 526-528]`；zip 型还要求 `ModEditorVersion` 非空 `[:539-540]`；归档型从二进制里读 mod 名 `[LoadArchMod.cs:71]` | 走归档，读二进制里的 mod 名 `[LoadUtil/LoadArchMod.cs:208]` | PC 有额外的"目录形态门槛" | 移动端没有这条门槛（也没这条通道） | 若补文件夹通道，务必一起补 `ModInfo.json` + `ModEditorVersion` 门槛 |
| 7 | JSON 解析 | **LitJson** → `JsonKVProvider`；归档走 `MapperObject` + `StringMapper` | 同一套已移植：`LoadUtil/KVProvider.cs`、`LoadUtil/MapperObject.cs`、`LoadUtil/StringMapper.cs` | 无 | 无 | — |
| 8 | 建对象 | 单一：`ScriptableObject.CreateInstance(type)` `[ModLoader.cs:835/1096, LoadArchMod.cs:501, LoadPreData.cs:47]` | **三条**：① shim `CstiICallFix.RealShims.CreateLike`（模板克隆）→ ② 自带兜底克隆 `Activator.CreateInstance(type, clone)` → ③ `IL2CPP.il2cpp_object_new` 空实例 `[LoadUtil/LoadArchMod.cs:227-320]`，计数 `ShimCreatedCount/FallbackCloneCount/FallbackCreatedCount` `[:341-343]` | 移动端多两条兜底 | 兜底路径建出的**空实例**，Unity 原生字段（`m_FileID`/`m_PathID`、`hideFlags` 等）状态可能与 PC 的 `CreateInstance` 不同；克隆路径则可能带入模板的残留字段 | ⚠️ **需移动端 il2cpp 校核**：日志里三条路径的计数（`[CREATE]` 行 `[LoadPatchMain.cs:442-445]`）可作判据；建议统计"每条路径建出的对象数量"，并对关键卡做字段读回 |
| 9 | **引用定位** | **严格分派**：GUID 桶 / 类型名字桶 / Sprite / AudioClip / WeatherSpecialEffect，**无兜底** `[WarpperFunction.cs:27-117]`；且 `Sprite` 走延迟队列 `[:41-45]` | **GUID 优先 + 名字索引兜底**：`Diag.ResolveByJsonForm` 先 `LooksLikeGuid(value)` → GUID 路径（mod 字典 → 游戏注册表）；否则查**声明类型的名字索引**，三级匹配（精确 → 忽略大小写 → 归一化去 `_`/`-`/空格）`[Diag.cs:3474-3497, 1642]`，明确"两者**绝不互相兜底**" | 移动端**更宽松**（多了名字索引与大小写/归一化兜底） | 定位问题时，**"名字兜底救活了"不代表 PC 也会救活**；反之 PC 能过的用例移动端一般也能过 | 排查"为什么 PC 行移动端不行"时，先看是不是**落在名字兜底上**（日志 `[NAMEIDX-SUM]` `[Diag.cs:2071-2092]`）；要做 PC 等价回归，建议临时关掉名字索引只留 GUID |
| 10 | 资源发现 | `Resources.FindObjectsOfTypeAll(typeof(ScriptableObject))` `[ModLoader.cs:401]` | 因 il2cpp 该 ICall 未注册 → 改用游戏自己的 `UniqueIDScriptable.AllUniqueObjects` 静态字典 `[LoadUtil/LoadResources.cs:74-106]`，失败才回退 `FindObjectsOfType` `[:108-131]` | 实现不同，目标表相同 | 移动端拿不到"非 `UniqueIDScriptable` 的原版 SO / 原版 Sprite / AudioClip"（除非走回退） | ⚠️ **需移动端 il2cpp 校核**：`SpriteDict`/`AudioClipDict` 里有没有**原版**资源（若有 mod 卡引用原版图，可能查不到） |
| 11 | 额外步骤 | `FillDropsList()` ×3 `[DoWarpperLoader.cs:82,146,210]`；`AddPerkGroup` `[ModLoader.cs:1786]`；`GraphicsManager.Init` / `GuideManager.Start` / `LoadLanguage` 三个钩子 | 有：`WarpperAllEditorMods` / GSM / `MatchAndWarpperAllEditorGameSrouce` `[LoadPatchMain.cs:371,383,399]`；`AddPerkGroup` `[:405]`；`GraphicsManagerInitPostfix` **默认开** `[:25, 533]`；`LocalizationManagerLoadLanguagePostfix` **默认关**（`SkipLocalizationPostfix=true` `[:23]`）；`GuideManagerStartPrefix` **默认关**（`SkipGuideStartPrefix=true` `[:24]`） | **PC 有而我们默认没有：本地化、引导条目、自定义角色注册** | Windy 的 `Localization/SimpCn.csv` 中文文本、`GuideEntry`、`PlayerCharacter` 在移动端**默认不注册** | 若要 Windy 完整效果，需打开这两个开关并确认 il2cpp 侧类型/方法存在 —— **需移动端 il2cpp 校核**。`FillDropsList` 是否在移动端被重跑，也建议在日志里显式打一条判据 |
| 12 | 交互判定补丁 | 有（Lua 闸门）：`CardAction::CardsAndTagsAreCorrect` / `CardOnCardAction::CardsAndTagsAreCorrect` / `CardAction::WillHaveAnEffect` / `DismantleActionButton::Setup` / 拖拽一族，但**前缀不匹配即 `ret`**，纯数据 mod 直通 | 未见对应 Lua 层；只有只读探针 `DragProbe.Install()` `[LoadPatchMain.cs:430]` | PC 的 Lua 扩展能力我们没有 | 对**纯数据 mod 无影响**（PC 上这些补丁对 Windy 也是 no-op） | 若未来要支持 Lua 型 mod，另立项；当前不必追 |
| 13 | 贴图 | 延迟队列 + `PostSetEnQueue` + 队列排空后 `CardGraphics.Setup()` 全量刷新 `[PostSpriteLoad.cs:65-75, 192-204]`；DDS(DXT1/DXT5) 支持 `[:130-145]` | 自建 `RawTexture` / `RawTextureRaw` / `IconPack`（il2cpp 无 `Texture2D.LoadImage` 时走 raw 通道）；卡面判据 `Diag.DumpModCardImages()` `[LoadPatchMain.cs:436]` | 实现完全不同 | 贴图是移动端已知的高风险区 | 保留现有 `[CARDIMG]` 判据 |
| 14 | 附加功能 | 模组管理器 UI（Ctrl+Tab）、自动更新、Lua、对话树、内置中文字体包 `[ModLoader.cs:193-223]` | 无 UI；有 `CheatListFix` 维护 `CheatsManager.AllCards` / `GameManager.AllCards` `[MiniLoader.cs:148-150, 176-178; LoadPatchMain.cs:176]` | PC 有一堆附加功能我们没有；我们多了控制台卡表维护 | 无功能阻塞 | — |
| 15 | 运行期错误处理 | 每个 key/每个对象都 try-catch 成 warning `[WarpperFunction.cs:460-463]`、`[DoWarpperLoader.cs:154-157]` | 同样逐条 try-catch，但额外有整段保护（GSM 整段吞异常）`[LoadPatchMain.cs:378-389]` | 移动端更保守 | — | — |

---

## ⑥ 结论

### 6.1 "数据写对就该生效"是否成立？

| 场景 | 是否成立 | 依据 |
|---|---|---|
| **新建 mod 卡（`CardData/xxx.json`）+ 普通字段/标量** | ✅ **成立** | `JsonUtility.FromJsonOverwrite` 一次写全 `[ModLoader.cs:844]`；游戏 `Init()` 由 `ClearDict` 后的循环兜底 `[PC游戏IL]` |
| **新建 mod 卡 + 引用字段（`*WarpData`/`*WarpType`）** | ✅ **成立**（前提：GUID/名字/图片名**逐字符正确**且目标已注册） | `JsonCommonWarpper` 递归解析 `[WarpperFunction.cs:142-465]`；查不到**静默失败**，不报错 `[:477-489]` |
| **新建 mod 卡 + `Sprite` 引用** | ⚠️ **成立但异步** | 引用入 `PostSetQueue` 排队 `[WarpperFunction.cs:41-45]`，解码完成后回放 `[PostSpriteLoad.cs:161-172]`；**加载完成前看起来"没图"是正常的** |
| **新建 mod 卡 + 需要出现在分类页/蓝图/统计页** | ❌ **不成立**，要额外注册 | `GraphicsManager.Init` postfix 里的 6 个 `Add*` `[ModLoader.cs:1832-1856]` |
| **新建 `CharacterPerk` / `PlayerCharacter`** | ❌ **不成立**，要数组扩容追加 | `AddPerkGroup` `[ModLoader.cs:1415-1429]`；`Gamemode.PlayableCharacters` `[DoWarpperLoader.cs:232-248]` |
| **`GameSourceModify` 改原版卡 + 只写普通标量** | ❌ **完全不生效** | `JsonCommonWarpper` 无标量分支 `[WarpperFunction.cs:388-458]`；除非加 `"ModLoaderSpecialOverwrite": true` `[DoWarpperLoader.cs:128-139]` |
| **`GameSourceModify` 改原版卡 + `MODIFY(5)` 容器** | ⚠️ **基本成立，但掉落类字段要重跑 `FillDropsList()`** | `[DoWarpperLoader.cs:82, 146, 210]` |
| **本地化 / 引导 / 自定义角色** | ❌ **不成立**，要等对应 Manager 钩子 | `[ModLoader.cs:1804-1828]` |

### 6.2 若 PC 也要额外步骤 → 点名（共 5 类）

1. **`CardData.FillDropsList()` 重跑** — 掉落/产出类字段改完必须重跑（private 方法，`Traverse` 调用）。`[DoWarpperLoader.cs:82, 146, 210]`
2. **`CharacterPerk` → `PerkGroup.PerksList` 数组扩容追加**（JSON 里的 `CharacterPerkPerkGroup` 是 loader 指令，不是游戏字段）。`[DoWarpperLoader.cs:217-223]` → `[ModLoader.cs:1415-1429]`
3. **`PlayerCharacter` → 每个 `Gamemode.PlayableCharacters` 扩容追加**。`[DoWarpperLoader.cs:232-248]`
4. **六类 UI/索引注册延迟到 Manager 钩子**：`GraphicsManager.Init`（卡牌页签 / 蓝图 / 统计 / 过滤器 / 内容页 / 自定义 GameObject）、`GuideManager.Start`（引导条目 / 角色）、`LocalizationManager.LoadLanguage`（CSV）。`[ModLoader.cs:1832-1856, 1816-1828, 1804-1814]`
5. **贴图延迟加载 + 卡面全量刷新**：Sprite 引用先入队，队列排空后 `CardGraphics.Setup()` 遍历刷新。`[PostSpriteLoad.cs:65-75, 192-204]`

**另外两个"隐性前提"（不满足则静默失败）：**
6. **每个 `UniqueIDScriptable` 必须带非空 `UniqueID`**，否则被跳过并打 `"... without GUID"` 错误。`[ModLoader.cs:826-833]`
7. **引用键必须逐字符正确且目标已注册**；查不到**不报错**。`[WarpperFunction.cs:477-489, 507-508]`

### 6.3 对移动端移植的三条行动建议

1. **先补齐"发现通道"再谈其他**：移动端只有 `.modArch_V3` 一条，Windy 原始文件夹形态进不来。用 `tools/archpack.py` 打包是最短路径；若要做 PC 等价回归，建议后续补 `LoadMods`（文件夹型）。
2. **把差异 ④（`Init()` 谁调）当作头号校核项**：PC 是"游戏替我们调 `Init()`"，移动端是"自己调且只覆盖 `AllGUIDDict`"。这直接决定 `FillDropsList()` 有没有被跑到 —— 而这正是 PC 侧唯一被显式重跑的"隐性步骤"。**需移动端 il2cpp 校核。**
3. **差异 ⑨（引用定位更宽松）是双刃剑**：名字索引兜底会让"本该失败的引用"在移动端成功，掩盖真实的键错误。做回归时建议临时只留 GUID 通道，与 PC 逐条对齐。

---

## 附录 A：本文引用的 PC 源码文件清单

| 文件 | 行数 | 作用 |
|---|---|---|
| `CSTI-ModLoader/ModLoader.cs` | 2033 | 插件入口、5 个 Harmony 补丁、4 条发现通道、`LoadGameResource`、延迟注册清单 |
| `CSTI-ModLoader/WarpperFunction.cs` | 577 | **`XxxWarpData`/`XxxWarpType` 的唯一遍历者**；`WarpType` 枚举与全部落地逻辑 |
| `CSTI-ModLoader/WarpHelper.cs` | 261 | 字段定位（`FieldFromCache`）、构造器缓存、按偏移直接读写 |
| `CSTI-ModLoader/ResourceLoadHelper.cs` | 133 | `LoadUniqueObjs`（预热异步读 `<TypeName>/*.json|*.jsonnet`）、`LoadPictures`、`SimpleOnce` |
| `CSTI-ModLoader/LoaderUtil/DoWarpperLoader.cs` | 258 | 三个 warp 总入口；`FillDropsList` 重跑；perk/stat/角色/蓝图/页签的分流 |
| `CSTI-ModLoader/LoaderUtil/LoadPreData.cs` | 177 | 预热扫描 + `LoadFromPreLoadData`（建 `UniqueIDScriptable` 并写 `AllData`） |
| `CSTI-ModLoader/LoaderUtil/PostSpriteLoad.cs` | 223 | 贴图延迟解码、`PostSetQueue` 回放、卡面全量刷新、DDS |
| `CSTI-ModLoader/LoaderUtil/ResourceDataLoader.cs` | 78 | wav/mp3/ogg → `AudioClip` |
| `CSTI-ModLoader/ExportUtil/LoadArchMod.cs` | 682 | `.modArch_V3/V2/V1` 解析（LZ4 块、`JsonsBLK` 分派） |
| `CSTI-ModLoader/ExportUtil/KVProvider.cs` | — | `KVProvider` 抽象与 `JsonKVProvider`（**LitJson**） |
| `CSTI-ModLoader/ExportUtil/MapperObject.cs` | 569 | 归档专用类型化 JSON 节点 + 字符串表 |
| `CSTI-ModLoader/ExportUtil/StringMapper.cs` | — | 归档字符串表 |
| `CSTI-ModLoader/ExampleModify.json` | — | `ModLoaderSpecialOverwrite` 的最小示例 |
| `DynamicModLoader/DynamicModLoader.cs` | 1367 | 运行期动态加载（本次未展开；与静态加载流程正交） |

## 附录 B：本文用到的"游戏侧"证据方法

用 `BepInEx\core\Mono.Cecil.dll`（游戏目录里自带）离线读取 `Assembly-CSharp.dll`，**不加载游戏、不运行游戏**：

```powershell
Add-Type -Path '<game>\BepInEx\core\Mono.Cecil.dll'
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('<game>\Card Survival - Tropical Island_Data\Managed\Assembly-CSharp.dll')
$t = $asm.MainModule.GetType('UniqueIDScriptable')
($t.Methods | ? Name -eq 'ClearDict')[0].Body.Instructions | % { "IL_{0:X4}: {1} {2}" -f $_.Offset,$_.OpCode,$_.Operand }
```

已取证的 IL：
- `UniqueIDScriptable::ClearDict` = `AllUniqueObjects.Clear(); LoadedIDs.Clear();`
- `UniqueIDScriptable::Init` = `RegisterID()`
- `UniqueIDScriptable::RegisterID` = `AllUniqueObjects[UniqueID] = this`（重复则记 `Duplicates`）
- `CardData::Init` = `base.Init(); FillDropsList(); CachedFilter.Clear();`
- `GameLoad/<AwakeWithLoadingScreen>d__53::MoveNext` = `ClearDict()` 紧接 `for AllData → Init()` 的完整循环

---

## 附录 C：勘误 —— 早前草稿（同路径 14:41 版本）中的错误结论

本文档交付前，同路径已存在一份早前草稿。经源码核对，该草稿有下列**与源码不符**的结论，特此纠正（**以本文为准**）：

| # | 早前草稿的说法 | 事实 | 证据 |
|---|---|---|---|
| 1 | "入口：`LoadArchMod.LoadAllArchMod()`" | 真正的入口是 `ModLoader.Awake()`，`LoadAllArchMod()` 只是 ClearDict 前缀里的**第 2 步** | `[PC源码:ModLoader.cs:225, 1762]` |
| 2 | "发现：`Directory.EnumerateFiles(<目录>, "*.modArch_V3", SearchOption.TopDirectoryOnly)`" | 是 **`SearchOption.AllDirectories`（递归）**，且共扫 **3 种扩展名**（`_V3`/`_V2`/无后缀） | `[PC源码:ExportUtil/LoadArchMod.cs:22, 36, 50]` |
| 3 | "PC 使用 **Json.NET**（`JsonData`）" | PC 用的是 **LitJson**（`using LitJson;` / `JsonMapper.ToObject`），不是 Newtonsoft.Json | `[PC源码:ModLoader.cs:19, 900, 1103, 1264]` |
| 4 | "**我们**用 `JsonUtility.FromJsonOverwrite`（Unity）" 暗示 PC 不用 | PC **同样大量使用** `JsonUtility.FromJsonOverwrite` 作为"整对象一次性反序列化"的主路径；`JsonUtility` 不是移动端特有 | `[PC源码:ModLoader.cs:844, 1110]` 等 8 处 |
| 5 | "`KVProvider`（Json.NET `JsonData` ↔ `KVProvider`）" | 是 **LitJson `JsonData`** ↔ `KVProvider` | `[PC源码:ExportUtil/KVProvider.cs]` |
| 6 | 引用分派写成"`field_type.IsSubclassOf(UnityEngine.Object)` → `AllGUIDDict`" | `UnityEngine.Object` 分支**不存在**；实际按 `UniqueIDScriptable` / 其它 `ScriptableObject` / `Sprite` / `AudioClip` / `WeatherSpecialEffect` / `ScriptableObject` 六类分派 | `[PC源码:WarpperFunction.cs:27-63]` |
| 7 | 文件路径写成 `CSTI-ModLoader\PostSpriteLoad.cs`、`CSTI-ModLoader\LoadPreData.cs` | 实际在 **`CSTI-ModLoader\LoaderUtil\`** 子目录下 | 文件系统核对 |
| 8 | 未提及 `WarpType` 0/1/2 是否实现 | **0/1/2 在 2.3.6.35 中未实现**（落到 `else` 报 `Unexpect WarpType`） | `[PC源码:WarpperFunction.cs:381-384]` |
| 9 | 未提及 `CardData.FillDropsList()` 重跑 | 这是 PC 侧**唯一被显式重跑的"隐性步骤"**，对"数据写对就生效"的判断至关重要 | `[PC源码:LoaderUtil/DoWarpperLoader.cs:82, 146, 210]` |
| 10 | 未提及 `plugins2` 与源码路径不匹配 | 2.3.6.35 **只读 `BepInEx\plugins`**，当前 F: 盘 `plugins2\` 布局不会被扫描 | 见本文 §0 |

---

## 14. 2026-10-03 实测结论（照 PC 三条通用通道的落地情况）

### 14.1 `ArrayResize` 通道 —— **已等价实现** ✓（不是根因）
* 现状：`SetArrNoWarpper`（`MainGenTools.cs`）本身即 PC `WarpperFunction.cs:570 ArrayResize` 的等价实现：
  读旧数组 → `Array.CreateInstance(elem, old+N)` → 逐元素复制（保原引用）→ 尾部追加 → 整块写回。`ADD_REFERENCE` 的
  "只追加已存在引用、不新建对象"语义由 `warpType != ADD_REFERENCE` 判定（`MainGenTools.cs:597`）保证。
* 第 A 轮（拆裸写）：原 `SetArrByWarpper` 里 `il2cpp_array_new` + "数组头偏移直接写槽位"（裸内存）→ 换成与 `:867`
  同款的纯托管写法（`Array.CreateInstance` + `SetValue` + 写屏障）。真机验证通过。
* 第 B 轮（口径）：在 `SetArrNoWarpper` 内接上 `[ARRAYRESIZE]` 计数与日志（不重构路径）。
* **真机判据**：`[ARRAYRESIZE] 扩容追加=64 失败=0 就地改=11`、引用保留 2/2、六项判据全绿 ⇒ **该通道健康，不是拖拽根因**。
* **"被替换 7"与本通道无关**：第 B 轮 diff 仅新增日志/计数（零写入路径改动）；且 A 轮验证时即为
  `451289 中保留 451282（被替换 7）`，与既有残留一致、未恶化 ⇒ 7 处替换另有来源，非本通道引入。

### 14.2 `模式=ADD_REFERENCE` 命中 0 行 —— **待查（低优先）**
* GSM 侧存在 3 处 `WarpType=ADD_REFERENCE`，但 `[ARRAYRESIZE] 模式=ADD_REFERENCE` 未出现。
* 可能原因（未验证）：那 3 处落在"无字段变化"分支、或走了 `SetLiByWarpper`/其它分支。
* 裁定：**暂不追**，留作低优先项。

### 14.3 `PostSpriteLoad` 延迟落盘 —— **实验结束、默认永久关、无收益**
* 复刻语义：`PostSpriteQueue.Enqueue`（warp 时入队）+ `Flush`（Tick 泵 / warp 收尾同步）；cfg `PostSpriteLoadQueue=true` 才启用。
* 第一轮（Tick 泵 flush）：`[POSTSPRITE] 入队=319 flush=286 失败=33`，`[CARDIMG] 有CardImage=0 无CardImage=174`
  （**图片全丢**）。原因 = **时机错位**：`[CARDIMG]` 快照 `15:49:59` 早于 `[POSTSPRITE] flush 15:50:22`
  ⇒ 队列值在游戏读取之后才写入、再没机会生效。PC 的 `CompressOnLate` 是 loader 自己阶段里的协程（早于游戏读取）。
* 第二轮（改同步 flush，位置在 `LoadPatchMain` 的 `[CARDIMG]` 之前）：恢复 `170/174`，但**与不启用时完全一样 ⇒ 无净收益**。
* 失败原因分布：全部为"弱引用已死（宿主存活=False 值存活=True）字段=`OverrideIcon`"
  ⇒ 队列宿主是 **warp 期间的临时对象**，flush 前已被回收 ⇒ 延迟写在移动端拿不到宿主。
* 裁定：**默认永久关**（当前默认即关 = 立即写 = `170/174` 可用状态）；代码保留作档案，**不再启用**。

### 14.4 `[PHASE2-ORIG]` 原版对照锚点问题
* 判定"26 张空掉落表本来就无掉落 vs 应有而无"时，按英文名去前缀找原版同类卡：结果 `找不到原版=100`
  ⇒ **锚点错误**：原版卡名是**本地化中文名**，按英文名查找必然落空。
* 正确锚点应为 `CardData.CardModel` / `CardType` 或同类原版卡的判定字段（未实施，留作低优先）。
* 相关：`FillDropsList()` 双时机重跑（`DropsFix.cs`，默认开）已把 `AllDrops 非空` 从 **74 → 148**；`AllDrops` 是掉落来源型字段，
  上述 26 张多为非掉落来源卡。

### 14.5 注册时机（"创建提前"）—— **独立课题**（证据：`append=0`）
* 目标（照 PC `ModLoader.cs:852`）：在 `UniqueIDScriptable.ClearDict` 前缀里把 mod 对象 append 进
  `GameLoad.Instance.DataBase.AllData`，让游戏自己的循环调 `Init()`（我们不自己调、不轮询）。
* 实测：`[PHASE2] 已 append 进 AllData: 成功=0 跳过=0 之后 AllData.Count=2858`
  ⇒ **那一刻我们的对象还不存在**（时间线：我们 init 15:21:26 → 游戏 `ClearDict` 15:21:30 → mod 对象创建完成 15:21:49）
  ⇒ 窗口存在（≈4 秒），但我们的流水线把"创建"排在重活（名字索引 4~6s、图集/61 sprite 4~8s、等注册表就绪的 deferred-init）之后。
* 裁定：**留作独立课题**（需把 create/warp 压进 `ClearDict` 之前的窗口；重排单独一轮、只带 `[PHASE-ORDER]` 计时）。
* 既有 `[PHASE-ORDER]` 时间线：`t0=melon init` / `t1=ClearDict 前缀开始` / `t2=我们自己的 Init 完成` / `t3=append 完成` / `t4=游戏 Init 之后`。

### 14.6 PC 侧三条通用通道的最终状态
| # | PC 机制 | 我们的状态 | 结论 |
|---|---|---|---|
| 1 | `ArrayResize` 原地扩容 + `ObjectAddReferenceWarpper` 追加语义 | **已等价实现**（A/B 两轮 + 真机判据全绿） | 不是拖拽根因 |
| 2 | `PostSpriteLoad` 延迟落盘 | 已复刻但**无收益**（宿主弱引用在 flush 前回收） | **实验结束、默认永久关** |
| 3 | 按字段类型的引用字典分层 | 部分（mod 字典 + 游戏注册表 + 名字索引） | 待评估（未做） |

