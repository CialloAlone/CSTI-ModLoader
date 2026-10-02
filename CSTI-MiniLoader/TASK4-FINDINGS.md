# task-4 诊断结论（2026-10-02 真机实测，warpper-loop）

设备：NZNBUC456PJN7HR4 / com.winterspringgames.survivaljourney
日志：`/sdcard/MelonLoader/<pkg>/MelonLoader/Latest.log`；诊断落盘：`/sdcard/MelonLoader/<pkg>/csti_trace.log`
（GameRootDirectory 在真机上是 app 私有外部目录，adb shell 读不了：`Permission denied`，tombstone_* 同样读不了 → 诊断要用 Mods 的父目录落盘）

## 1. 「新特质看不到」的真正原因链

1. **`CharacterPerkPerkGroup` 在本版本游戏里已经不存在**。Windy 的 6 个 CharacterPerk JSON 全量键里没有它
   （只有 PerkName/PerkIcon/AddedCards/StartingStatModifiers/…），所以 `AddPerkGroup` 的
   `WaitForAddPerkGroup` 队列永远是 0 条 —— 老版「按 PerkGroup 挂特质」的机制整体失效。
2. **本版本特质页签是 `PerkTabGroup`**：
   - `[GEN] PerkTabGroup 字段(4) = TabName, Icon, IncludesAllPerks, ContainedPerks`
   - 注册表 4 个页签：`PkTab0_All`(IncludesAllPerks=True, ContainedPerks 空, 运行时填充)、
     `PkTab1_Conditions`(35)、`PkTab2_Traits`(29)、`PkTab3_Background`(23)
   - **`PerkTabGroup.ContainedPerks` 才是「特质显示列表」**；mod 数据里没有任何字段能塞进它 → 必须在 warp 后手动登记。
3. `ItemDictionary(typeof(PerkGroup))` 里只有 mod 自己那 1 个 PerkGroup（GUID 键），按组名查必然落空。

## 2. 顺带修掉的 3 个上游缺陷（都真实影响内容写入）

| # | 位置 | 问题 | 修法 |
|---|------|------|------|
| A | `MainGen.GetOrGen` | 存的是 `NativeFieldInfoPtr_*`（IntPtr 静态字段）的 FieldInfo，`tuple.fld.FieldType` 恒为 `System.IntPtr` → `isValueType` 恒 true、`GetGenericTypeDefinition()` 对 IntPtr 抛异常 → **所有数组/List 型「引用」字段 warp 全灭**（内部异常 275） | 真实类型从代理类同名**属性**取；`isValueType` 用 il2cpp 原生元数据判定 |
| B | `MainGenTools.SetArrByWarpper/SetLiByWarpper/SetByWarpper/SetLiNoWarpper/SetArrNoWarpper` | 用 `GetOrGen(typeof(T))`（元素类型）查字段偏移，宿主其实是 baseObj → 静默 return；`SetLiByWarpper` 还算完 list 从不写回 | 改成 `baseObj.GetType()`；补写回 |
| C | `MainGenTools` 引用解析 | 只查 `AllItemDictionary[typeof(T)]`，而游戏自带 2858 个对象是按 GUID 存在 `UniqueIDScriptable.AllUniqueObjects`（包装类型统一成 UniqueIDScriptable）→ 引用全部解析失败 | 新增 `TryResolveRef<T>`：先查 mod 字典，再回查游戏注册表 |

## 3. 两个真机 SIGSEGV（务必别踩回去）

1. **内联值类型字段不能当指针解引用**：IL2CPP 的 struct 在 interop 代理里是**类**，用
   `PropertyType.IsValueType` 判断会误判成引用类型 → `*(IntPtr*)(obj+offset)` 读到 struct 首 8 字节当指针 → SIGSEGV。
   实测崩溃点：`CardAction.RequiredReceivingDurabilities(ft=DurabilityConditions)`。
   → 必须用 `il2cpp_field_get_type` → `il2cpp_class_from_il2cpp_type` → `il2cpp_class_is_valuetype` 判定；
   值类型子对象直接跳过 warp（不读不写）。
2. **数组分配/填充**：`new Il2CppReferenceArray<T>(n)` + 托管索引器 `newArr[i]=item` 在真机上填充阶段 SIGSEGV。
   → 改用字段自身的 il2cpp 类型分配并直接写槽位：
   `il2cpp_array_new(il2cpp_class_from_il2cpp_type(il2cpp_field_get_type(fPtr)), n)`
   + 元素基址 `arrPtr + (IntPtr.Size==8 ? 0x20 : 0x10) + i*IntPtr.Size`，用 `il2cpp_gc_wbarrier_set_field` 写。
   （`IL2CPP` 里没有 `il2cpp_gc_wbarrier_set_arrayref`。）

## 4. 修好之后的实测证据（20:11 轮）

- 基线：pid 非空、`threw an exception`=0、`类型初始化失败`=0、`增量 204`、`加载 Windy 成功`、`[STEP] 9 done`
- `[WARP统计] 处理=204 json空=0 已warp=204 异常=0 | warp: 键=164671 写入=8076 跳过(无字段)=34678 内部异常=145 空gen表类型=145`
  （内部异常 275→145，空 gen 表 5303→145）
- `[DESC] PerkGroup name=Windy_start PerksList = [17 项]`（此前是 0 项）
- `[MP] Windy_air AddedCards=[9 项]`、`Windy_bow=[6 项]`、`Windy_bless=[1]`、`Windy_Windy=[1]`
- `[TAB] PkTab0_All 0→6 / PkTab1_Conditions 35→41 / PkTab2_Traits 29→35 / PkTab3_Background 23→29`
- 数组写回成功 280 次（`csti_trace.log` 的 `[ARR] 写回完成`）

## 5. 仍待处理

- `Windy_desert` / `Windy_Forest` 的 `AddedCards=0`（其 JSON 里可能就没有 AddedCardsWarpData）。
- 图标（Sprite）无法注入：裁剪版引擎 Sprite ICall 缺失，`PerkIconWarpData="entrance"/"dust"` 解析不到 → 图标为空。
- 「开局精灵」同理受限（Sprite::CreateSprite / Texture2D::LoadRawTextureData ICall 缺失）。

## 6. 最新一轮实测（20:13，内部异常归零）

- `[WARP统计] 键=166663 写入=8273 跳过(无字段)=35769 内部异常=0 空gen表类型=197`
  （历史：内部异常 275 → 145 → **0**；空 gen 表 5303 → 197）
- `[TABCHK] PkTab0_All=6 / PkTab1_Conditions=41 / PkTab2_Traits=35 / PkTab3_Background=29` 连续采样稳定
- 修掉 `CardData.DismantleActions → ArgumentException: IList 不能转 List<T>`：
  WarpFunc 的 List/数组分支不再把 `Cast<IList>()`/`Il2CppSystem.Array` 包装写回具体字段类型
  （`list[i]=ele` / `array.SetValue` 本来就是原地修改，已生效）。

## 7. 自我修复登记（20:15 起）

`Diag.CheckTabCountsTick()` 每 4 秒（共约 16 分钟）遍历 4 个 PerkTabGroup：
用**指针比对**（只用索引器，不依赖泛型 Contains）找出缺失的 mod 特质并 `Add` 回去，
若发现补登记就打 `[TABFIX]`。这样即使游戏在打开特质界面时重写/清空 `ContainedPerks`，
也会在 4 秒内自动恢复，不需要用户重启。

## 8. 特质界面的真正数据源（20:16 用 `[SCAN]` 扫游戏程序集得到）

```
GameManager.AllPerks          : List<CharacterPerk>
GraphicsManager.UnlockedPerksQueue : List<CharacterPerk>
MainMenu.AllCharacterPerks    : List<CharacterPerk>     ← 角色创建界面全量特质
MainMenu.UnlockedPerks        : List<CharacterPerk>     ← 已解锁特质
MainMenu.AllPerkTabs          : List<PerkTabGroup>      ← 特质页签
MainMenu.CurrentlyEquippedPerks : List<CharacterPerk>
PerkTabGroup.ContainedPerks   : List<CharacterPerk>
PerkGroup.PerksList           : Il2CppReferenceArray<CharacterPerk>
GameStat.RequiredPerks / PlayerCharacter.CharacterPerks / MenuPerkButton.AssociatedPerk …
```
所以除了 `PerkTabGroup.ContainedPerks`，**`MainMenu.AllCharacterPerks` / `UnlockedPerks` 也必须补**。
`Diag.EnsureModPerksInMainMenu()` 已加入周期维护（`FindObjectsOfType<MainMenu>()` 在本机可用）。

实测（20:17 轮）：`[MENUFIX] MainMenu 补登记: AllCharacterPerks+6 UnlockedPerks+6（AllPerkTabs=4; AllCharacterPerks=95; UnlockedPerks=26）`
→ 89 个原版特质 + Windy 6 个 = 95。

## 9. 特质名不依赖 CSV（好消息）

`CharacterPerk.PerkName` 是 `LocalizedString`，实测字段：
```
ParentObjectID = "8adb7a05993748ba8827f2b91128c96d"
LocalizationKey = "Pk_1_Windy_PlaneCrash_PerkName"
DefaultText     = "精灵之森坠机"       ← 中文文本直接内嵌
LocalizedText   = null
```
`[L10N] 文本总数=14339 含 Windy 的键=0` → mod 的 CSV 没进 `LocalizationManager.CurrentTexts`
（`LoadLocalizationPublic` 只处理文件名含 `SimpCn` 且当前语言为「简体中文」的条目），
但 `DefaultText` 里有中文 → 界面应能显示「精灵之森坠机 / 精灵的祝福」等名字。

## 10. 图标（已解决，20:29）

### 10.1 libunity.so 的判据（Lead 指定方向的结论）
- `.dynsym` 343 条（几乎全是 import，带地址的只有 5 条如 `JNI_OnLoad`）；`.symtab` = **0**（已 strip）；
  动态/静态符号表里 Sprite/Texture2D/AudioClip 相关符号 **0 条** → 「把缺失 ICall 绑回导出符号」这条路**不存在**。
- .so 里仍留有 ICall 名字字符串（`Sprite::CreateSprite_Injected`、`Texture2D::LoadRawTextureDataImpl(Array)`、
  `AudioClip::Construct_Internal` 等），但 **.rela.dyn 26621 条里没有任何重定位指向这些字符串**
  → 引擎里已不存在 {名字,函数} 注册表，无法从表里恢复函数地址。
- 工具：`tools/elfscan.py`（ELF 符号/字符串扫描）、`tools/elftable.py` / `elftable2.py`（名字布局 + 重定位交叉引用）。

### 10.2 真正的答案：**只是 ICall 名字对不上**
`il2cpp_resolve_icall` 用变体名探测（156 个候选）**命中 12 个**：
```
UnityEngine.Sprite::CreateSprite_Injected      ✓
UnityEngine.Sprite::get_texture                ✓
UnityEngine.Sprite::get_rect_Injected          ✓   get_bounds_Injected ✓
UnityEngine.Texture2D::SetPixelsImpl / ApplyImpl / Internal_CreateImpl   ✓
UnityEngine.ImageConversion::LoadImage / EncodeToPNG                     ✓
UnityEngine.Resources::Load / Mesh::Internal_Create / Shader::Find       ✓
```
→ 裁剪只砍了 managed 侧解析的 234 个**注册名**，等价实现以 `_Injected`/`Impl` 名字注册着。

### 10.3 托管包装不能走（会 SIGSEGV）
`Sprite.Create`（托管）与 `ImageConversion.LoadImage`（托管）内部解析的是不存在的名字，
真机调用直接 SIGSEGV（两次实测）。**必须直接调已注册的 ICall**：
```csharp
// 建精灵
var d = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float, uint, int, float*, byte, IntPtr>)fn;
sprite = d(tex, rect, pivot, 100f, 0u, 0, border, 0);
// 传像素：bool f(Texture2D* tex, Il2CppArray* data, bool markNonReadable)
```

### 10.4 图形管线（Windy 图集是 8192×8192 DXT5 + mip，dataLen=89478512）
原来的 `LoadRawTextureData`（3 个 ICall 全缺）→ 必然失败；托管 `Sprite.Create` → SIGSEGV。
现方案（`IconPack.cs`）：
1. 按每个精灵的 rect 从 DXT5 图集**只解小块**（避开整张 8192² 解码），得到 RGBA32；
2. managed 编 PNG（`ZLibStream` + 手写 CRC32，filter=0）；
3. `il2cpp_array_new(System.Byte[])` + 直接调 `ImageConversion::LoadImage` 上传；
4. 直接调 `Sprite::CreateSprite_Injected` 建精灵，塞进 `ItemDictionary(typeof(Sprite))`。

**真机实测（20:29）**：
```
[IMG2] 精灵创建完成: 成功=56 失败=0 / 共 56
[IMG2] 精灵创建完成: 成功=4 失败=0 / 共 4
[自检] Sprite 全量键(60): slime, stick, home, exit, bed, …, entrance, dust, bow, arrow, Icey, Wing, peach, Windy
```
`entrance` / `dust` 正是 Windy 特质的 `PerkIconWarpData` ✓；`bow/arrow/…` 是物品图标 ✓。
基线同时保持：pid 非空、threw an exception=0、类型初始化失败=0、增量 204、加载 Windy 成功。

### 10.6 重要反例：libunity.so 的「名字字符串」≠ 注册表

`tools/icallnames.py` 把 libunity.so 里所有 `X::Y` 形式的名字枚举出来（**5329 个**），
缺清单 234 个里有 **229 个名字原样存在**——但真机 `il2cpp_resolve_icall` 只对少数名字返回非 0。
→ 字符串只是引擎自带的名字表，**注册项确实被裁掉了**，不能凭字符串判断「可用」。

缺清单里真正「引擎中没有同名方法」的只有 4 个：
`Object::FindObjectOfType`、`Sprite::CreateSprite`、`Sprite::Internal_CreateSprite`、`Texture2D::LoadRawTextureData`
（这 4 个恰好就是我们踩坑的那几个；旁边都有**已注册的等价实现**可用。）

### 10.7 音频结论：按名字绑定不可行（有证据）

- 音频的 AudioClip ICall 名字（`Construct_Internal`/`CreateUserSound`/`GetData`/`SetData`/`LoadAudioData`…）
  在 libunity.so 字符串表里**全都在**；
- 真机探测 19 个音频基础名 × 6 种变体 = **114 个候选，命中 0**；
- `.dynsym` 无导出、`.symtab` 已 strip、`.rela.dyn` 无指向这些名字的重定位 → **拿不到函数地址**。
→ 音频要恢复只能靠「别的已注册等价 ICall」（目前没探到）或改引擎/换包，不建议继续投入。

## 11. 回归：游戏事件/掉落/天气被写坏（20:35 报告 → 20:43 根治）

症状：开局事件选「请给我药物」「要压缩肉干」没反应；探索没掉落；改变天气无效。

**根因（两层）**：
1. mod 对象是用 `Object::Internal_CloneSingle` 克隆**游戏现成资产**得到的 = **浅拷贝**：
   克隆体的引用类型字段（数组/List/子对象）与游戏资产**共享同一个实例**。
2. 我修好 `MainGen` 字段真实类型后，原本是死代码的「嵌套子对象 warp」「List 元素原地改」被激活
   （实测 `嵌套对象原地改=1939 次、List 原地改=197 次`），于是写入落到共享对象上 = 直接改游戏数据。
   再叠加 `JsonUtility.FromJsonOverwrite(json, 克隆体)` 与 `CardData.FillDropsList()` 也写共享容器
   → 事件、掉落表、天气全被覆盖。

**排除**：`GameSourceModify`（曾加 `CleanName`）**一直没生效**：`[GSM] 本会改到游戏对象=0 保持inert=63`。

**根治（Lead 指定方案，已实现 `Diag.NeutralizeClone`）**：
克隆之后、**任何写入之前**，把克隆体所有引用类型数据字段就地换成**全新空实例**，绝不克隆元素：
| 字段类型 | 处理 |
|---|---|
| 数组 / `Il2CppReferenceArray<T>` | `il2cpp_array_new(元素类, 0)` |
| `List<T>` / `Dictionary<K,V>` | 新建空容器（`Activator.CreateInstance`） |
| `string` | 置 null |
| 纯托管类子对象（LocalizedString / CardAction…） | `il2cpp_object_new(cls)` 建**空实例**（非克隆） |
| `UnityEngine.Object` 派生（Sprite/CardData/Gamemode…） | **不动**（资产引用，由 WarpData 解析写入） |
| 值类型 | 不动 |

**判据（真机 20:43）**：
```
[NEUTRAL] CardData 新建容器/实例=72（数组 42 列表 8 子对象 22）；总计 12720
[INVARIANT] 采样游戏自带对象 136 个；warp 后 变化=0 / 136（签名总和 900→900） ✓ 完全不变
[GSM] 本会改到游戏对象=0 保持inert=63
```
特征保持：`精灵创建完成 56+4`、`Sprite : 60 项`、`[TAB]` 4 页签各 +6、`[MENUFIX] AllCharacterPerks=95`、
`Windy_air AddedCards=[9 项]`、`DefaultText="精灵之森坠机"`。基线：pid 存活、异常 0、增量 204、加载 Windy 成功。

**正向副作用**：mod 卡牌不再"继承"模板卡的集合数据（原来是共享的），数据更干净。

**仍保留的保守开关**（若仍需排查可回退）：
- `WarpFunc` 嵌套子对象原地 warp：停用（`嵌套对象原地改已跳过`）；
- `WarpFunc` List 元素原地改：停用（`List原地改已跳过`）；
- `GameSourceModify`：inert。
有了 NeutralizeClone 之后这三条理论上可以逐步放开（写入只会落在 mod 私有对象上），但需要用户每步验收。

---

## 12. 二分实验记录（Lead 指定口径，2026-10-02 20:50~）

### 12.1 二分 1：移走 Windy 包（只移 mod 包，6 个 DLL 全留）
- 目的：区分「破坏来自处理 Windy 数据」还是「来自 loader/泵/CstiICallFix 本身」。
- 口径修正（用户指出）：被移走的包里就包含那个"开局事件"，所以移走后**无法再触发那个事件** → 改用**游戏原生交互**做判据。
- 判据 A（首选）：新档做一次**原生掉落行为**（探索/采集，如沙堆收集沙子）是否正常产出。
- **结果：原生「沙堆收集沙子」正常产出 ✔** → 我们的加载器/泵/ICall 接管本身**不破坏游戏逻辑**；
  破坏来自**处理 Windy 数据的过程** → 进入二分 2。
- 无 Windy 那一轮的状态：`[STEP] 0..9 done`、`threw an exception=0`、pid 存活、
  `[WARP统计] 处理=0`、`增量 0`、`Sprite : 0`、`mod 特质=0`（**均为无 mod 时的预期值**，不是回退）。

### 12.2 二分 2a：不把 mod 对象塞进游戏主数据表
- 改动：新增 `MiniLoader.SkipGameDataBaseAdd`（默认 true），跳过 `GameLoad.Instance.DataBase.AllData.Add(card)`；
  mod 对象仍进 `AllGUIDDict` / 自建字典 / 游戏注册表（`Init` 时）。
- 运行态证据：`[DB] 二分 2a：跳过 GameLoad.Instance.DataBase.AllData.Add(card)`；
  基线保持：`加载 Windy 成功`、`增量 204`、`Sprite : 60 项`、4 页签各 +6、`[MENUFIX] AllCharacterPerks=95`、
  `[INVARIANT] 尺寸/内容均完全不变`。
- 待用户按判据 A（原生探索）+ 开局事件复测。

### 12.3 E2 探针（路线 C 第一步：确认"UI 预览"与"实际结算"是否同源）
- `E2Probe.cs`：给 `GameManager.GetCollectionDropsReport`、`CardsDropCollection.FillDropList`
  各挂**只打日志**的 Harmony Postfix（不改返回值、不写任何游戏对象）；日志前 20 次全打、之后每 100 次一条。
- **实测：两个补丁都挂上了**（`[E2] 已挂 Postfix: ...`），进程不崩、基线不变
  → 历史"挂补丁即崩"只限 `LocalizationManager.LoadLanguage` / `GuideManager.Start` / `GraphicsManager.Init` 三个方法。
- 日志里其它 `HarmonyException: IL Compile Error` 属**别的 mod**（ShowContainerOverfill 的 transpiler 找不到
  `get_MaxWeightCapacity`；`RegisterTypeOptions.set_LogSuccess` 是 Il2CppInterop 版本不匹配的老问题）。

### 12.4 路线 C 的读点签名（`[E2SIG]` 实dump）

```
GameManager.GetCollectionDropsReport(CardAction _Action, InGameCardBase _FromCard, Boolean _CheckEnvironment) : CollectionDropReport
CardsDropCollection.FillDropList(Boolean _CheckEnvironment, Int32 _Multiplier) : Void

CollectionDropReport(class) : TickInfo:Vector3Int, FromCard:InGameCardBase, FromData:CardData, FromAction:CardAction,
                             DropsInfo:Il2CppReferenceArray<CollectionDropInfo>, TotalValue, BaseValue, RandomValue:Single, SelectedDrop:Int32
CardDrop(class)             : DroppedCard:CardData, Quantity:Vector2Int
CollectionDropInfo(class)   : CollectionName, IsSuccess, RevealInventory, BaseWeight, StatWeightMods, CardWeightMods,
                              DurabilitiesWeightMods, RangeUpTo, Drops:Il2CppReferenceArray<CardDrop>, CollectionUses, StatMods
CardsDropCollection(class)  : CollectionName, CountsAsSuccess, RevealInventory, CollectionUses, CollectionWeight,
                              StatsDropChanceModifiers, CardDropChanceModifiers, DurabilitiesDropChanceModifier, CreatedLiquid,
                              DroppedCards:Il2CppReferenceArray<CardDrop>, DroppedEncounter:Encounter, StatModifications,
                              DurabilityModifications, _CurrentDrop_k__BackingField:List, _CurrentSaveDataDrop_k__BackingField:List …
EncounterResultEffect(class) : ResultLog, DroppedCards:List<CardDrop>, StatChanges, TransferValuesToStats, AmmoRecovery
```
→ `CollectionDropReport` 是引用类型返回，Postfix 可 `ref CollectionDropReport __result` 后**新建**一条
`CollectionDropInfo` 挂进 `DropsInfo`（新数组 + 新对象，不改游戏已有对象）——与 B 路线同款结构性安全手法。

---

## 13. 深拷贝式去共享（21:04，最终修法）

**机理**：mod 的 JSON 是「Unity 序列化格式 + 引用占位对」：
`"PerkIcon": {"m_FileID":0,"m_PathID":0}` + `"PerkIconWarpData":"Windy" / "PerkIconWarpType":3`。
Unity 的 `FromJsonOverwrite` **不认识 `…WarpData`** → 真实引用只能靠 loader 的 **warp** 解析回填。

**根因（Lead 读 mod 源码 + 我们日志双向确认）**：`Event_Gift.json` 的三个选项放在
`DismantleActions[0..2]`（嵌套对象），每个的 `ProducedCards[0]` 是 WarpData 引用（Weather/Drug/Jerky）。
我上一版把数组**清成 0 长度**、又停用了嵌套 warp → 没有对象可写、引用永不解析 → 三个选项全无产出。

**最终实现（`Diag.DeepDetach`，递归深度≤3）**：
| 字段类型 | 处理 |
|---|---|
| 数组 / `Il2CppReferenceArray<T>` | **等长**新数组；元素用 `il2cpp_object_new(元素类)` 建**同类型新实例**并递归；元素是 `UnityEngine.Object` 资产则保持共享 |
| `List<T>` / `Dictionary<K,V>` | 新空容器（元素由 JSON/warp 填充） |
| `string` | null |
| 纯托管类子对象 | `il2cpp_object_new` 同类型新实例 + 递归 |
| `UnityEngine.Object` 派生 / 值类型 | 不动 |

并**重新启用** `WarpFunc` 的嵌套子对象 warp 与 List 元素 warp（写入只落在 mod 私有副本上）。

**正向判据（真机 21:04）**：
```
[EFFECT] EVENTCARD Windy_Event_Gift DismantleActions 条数=3
  [0] ActionName="请改变天气吧！"   ProducedCards=1 首个集合名=Weather 指针=0x7714907C00
  [1] ActionName="请给我药物吧！"   ProducedCards=1 首个集合名=Drug    指针=0x7714907A80
  [2] ActionName="想要一份压缩肉干" ProducedCards=1 首个集合名=Jerky   指针=0x7714907900
[EFFECT] Windy_Event_Gift_Drug: "想要止痛药。"→Painkillers / "想要抗生素。"→Antibiotics / "想要解毒的药。"→Antidote
```
**污染判据仍通过**：`[INVARIANT] 尺寸变化=0 内容变化=0 丢失=0 / 76 ✓`
`[WARP统计] 键=166663 写入=8273 内部异常=0；List原地改已跳过=0 嵌套对象原地改已跳过=0`；`[NEUTRAL] 244252`。

## 14. Windy 包离线解包事实（`tools/archdump.py`，零设备）

- 区块顺序：`ImgBLK → JsonsBLK → LocalBLK → AudioBLK → LuaBLK`
- ImgBLK：2 个图集（8192×8192 fmt=12，89,478,512 B），精灵 56 + 4；无单图
- JsonsBLK `listStr[0]` 分类：CardData 172、GameSourceModify 63、ScriptableObject 19、
  GameStat 17、CharacterPerk 6、Encounter 4、SelfTriggeredAction 4、ModInfo.json 1、PerkGroup 1
- LocalBLK：1 条 `SimpCn.csv`（82,719 字符），键形如 `Windy_Windy_CardDescription`
  （游戏非「简体中文」语言时 loader 不会注入 → `[L10N] 含 Windy 的键=0`）
- AudioBLK：1 个子块（解码后 1,705,832 B）
- **LuaBLK：条目数 0** —— 包里没有 Lua 脚本（按 mod 自己的读取代码解析，内层首个 int32 即 0）
- `Windy_Event_Gift` JSON 里 **没有 `AllDrops` 键**（114 键全量转储确认）；效果在
  `DismantleActions`（3 个嵌套对象）+ `ProducedCards*WarpData` 引用里；`CardInteractions = []`（JSON 本身为空）

---

## 15. 5 层引用链打通（21:15，判据全绿）

事件选项的真正产物在**第 5 层**：`卡 → DismantleActions[] → ProducedCards[] → DroppedCards[] → DroppedCard`。
修了 **三处**断点（A/B 是 Lead 清单，★ 是 `[CHAIN]` 轨迹抓出来的真凶）：

| # | 断点 | 修法 |
|---|---|---|
| A | `MainGen.GetOrGen` 只收 `GetDeclaredFields`（本类自有 `NativeFieldInfoPtr_*`），`ProducedCards`/`ActionName` 在**基类 `CardAction`** 上 → gen 表 miss，不下潜 | 沿继承链逐层收集（子类同名优先），属性类型也沿继承链找。证据：`DismantleCardAction` 字段(6)→(56) |
| B | `WarpFunc` 下潜判定用 `fldType.IsArray`，而 `Il2CppReferenceArray<T>` 在 interop 里是**类** → 永不命中 | 增加 `Diag.IsIl2CppArrayTypePublic`（`Il2CppArrayBase<>` 判定，与 `MainGenTools.CommonSet` 统一）；List / 托管数组 / Il2CppReferenceArray 三路都下潜 |
| **★** | 数组取出的元素包装类型是 **`Il2CppSystem.Object`**，`GetOrGen(Object)` gen 表为空 → 递归 warp 全跳过。轨迹：`[CHAIN] 对象字段 Object.ActionName` → `↳ 字段不在 gen 表` | 新增 `Diag.Retype(元素)`：按 il2cpp 真实类名重建同类型代理（`FindTypeByName` + 缓存）；两条下潜路径先 Retype 再递归，写回仍用原对象（同一原生指针） |

**判据（真机 21:15）**：
```
[EFFECT2] "请改变天气吧！"   → DroppedCards 长度=1; DroppedCard=Windy_Event_Gift_Weather Quantity=(1,1)
[EFFECT2] "请给我药物吧！"   → DroppedCards 长度=1; DroppedCard=Windy_Event_Gift_Drug    Quantity=(1,1)
[EFFECT2] "想要一份压缩肉干" → DroppedCards 长度=1; DroppedCard=Windy_Jerky              Quantity=(1,1)
[EFFECT2] "想要止痛药。/想要抗生素。/想要解毒的药。" → Painkillers / Antibiotics / Windy_Antidote 全部非空
```
基线同时保持：pid 存活、异常=0、`增量 204`、`Sprite 60`、4 页签 +6、`AllCharacterPerks=95`、`[INVARIANT] 0 变化`。

**仍待办（C，未做）**：原版资产按 `.name` 引用解析不到 —— `[RESOLVE] CardTag id=tag_Decoration → 未解析`、
`EquipmentTag eTag_Neck`、`AudioClip StoneAppear/ContainerBags`、`Sprite BG_SandTop` 均如此
（注册表只有 GUID 键）。需建"类型+名字"二级索引（`il2cpp_class_get_name` + `.name`），否则 `CardTags` 被写成空数组。




**排除的猜想**：`GameSourceModify`（上轮给它加了 `CleanName`）**一直没生效** ——
`[GSM] 本会改到游戏对象=0 保持inert=63`，清洗路径名后仍解析不到目标 GUID，不是污染源；为保守起见保持 inert。

**修法（恢复「修复前」的写入范围，不动已验收的特质/图标）**：
1. `WarpFunc` 嵌套子对象分支：只统计不写；子对象字段值仍由建对象时的 `JsonUtility.FromJsonOverwrite` 提供；
2. `WarpFunc` List 元素原地改：停用；
3. `LoadArchMod`：`GameSourceModify` inert；
4. 试过并**放弃**：`Diag.DetachPlainClassChildren` —— 用 `Internal_CloneSingle` 克隆纯托管类会 SIGSEGV
   （该 ICall 只对 `UnityEngine.Object` 有效），已删调用。

**修复后实测（20:39）**：pid 存活、异常 0、增量 204、`Sprite : 60 项`、`精灵创建完成 56+4`、
`[TAB]` 4 个页签 +6、`[MENUFIX] AllCharacterPerks=95`、`PerkIcon=Sprite name=entrance/dust`、
`Windy_air AddedCards=[9 项]`、`DefaultText="精灵之森坠机"` —— 特质与图标全部保持。

**遗留（基线就存在）**：`JsonUtility.FromJsonOverwrite(json, clone)` 也会写进共享子对象；
根治需要给克隆体做深拷贝（绕开 Unity 浅拷贝语义），风险较高，暂不动。

## 12. 下一版已备好

- 单图分支（`itemFlg==1`）也改成「DXT5/RGBA32 → PNG → `LoadImage` → `CreateSprite_Injected`」；
- `[IMG2]` 抠图日志加了**像素统计**（均值 RGBA + 有效像素%），判断图标是否真的可见；
- 音频 ICall 变体探针（结论见 10.7）。

### 10.5 顺带修掉的命名 bug（真机踩坑）

arch 里存的是 **Windows 路径**（`D:\SteamLibrary\...\Resource\Picture\slime`），
Android 上 `Path.GetFileNameWithoutExtension` 不认反斜杠 → 整个路径成了文件名与字典键。
新增 `LoadArchMod.CleanName()` 统一取干净名字（精灵名、obj_name、CardName、GameSourceModify 的 Guid）。




---

## 16. C：按「类型+名字」解析原版资产（21:17 本地实现，待推）

**动机（真机 `[RESOLVE]` 未解析项与 mod JSON 双向印证）**：mod JSON 里**引用有两种形态**：
- **GUID 形态**（32 位 hex）→ 走我们的注册表（`AllUniqueObjects` / `AllItemDictionary`）✓ 已能解析：
  `AddedCardsWarpData`、`PerksListWarpData`、`OverrideEnvironmentWarpData`、`SpawningBlockedByWarpData` …
- **名字形态** → 原版资产（不派生自 `UniqueIDScriptable`，注册表里没有）✗ 一直解析不到：
  ```
  CardTagsWarpData      = ["tag_Decoration","tag_DecorationAdv","tag_Pretty"]
  EquipmentTagsWarpData = ["eTag_Neck"]
  CardBackgroundWarpData= "BG_SandTop" / "BG_SandFront"
  WhenCreatedSoundsWarpData = ["StoneAppear","ContainerBags"]   （laugh 是 mod 的，能解析）
  ```
  → `CardTags` 会被写成**空数组**。

**实现**
1. `Diag.EnsureNameIndex(typeName)`（惰性、每类型一次）：遍历 `AllUniqueObjects` 的 2858 个对象，
   按其 gen 表的**字段元素类型名**筛出目标类型的引用（所以 `CardTag`/`Sprite`/`AudioClip` 这种
   **不派生 UniqueIDScriptable** 的类型也能从「别的对象身上」被抓出来），按对象真实 `.name` 建索引；
   下探深度 ≤2（够到动作里的 `ActionSounds`），30 万次访问预算，重名保留第一个并统计冲突。
2. `MainGenTools.TryResolveRefByName<T>`：GUID 查不到 → 按 `(类型名, 名字)` 再试（类型名不匹配时退基类名）。
3. 三处写入路径统一兜底：`SetByWarpper`（单引用）、`List<T>`、`SetArrByWarpper`（数组）。
4. 判据行：`[TAGS] <卡>.CardTags = N 项: tag_…`。

**验收判据（下次运行）**
```
[NAMEIDX] CardTag 扫描对象=2858 建索引=… 冲突=… 耗时=…ms ✓ 可按名解析
[NAMEIDX] ✓ 按名解析 CardTag "tag_Decoration" / Sprite "BG_SandTop" / AudioClip "StoneAppear"
[TAGS] EVENTCARD …CardTags = 3 项: tag_Decoration, tag_DecorationAdv, tag_Pretty
[EFFECT2] 三个选项 DroppedCard 仍非空（上一轮绿色判据不许回退）
[INVARIANT] 尺寸变化=0 内容变化=0 ✓
```
**D 未动**：`GameSourceModify` 63 条仍 inert（"改游戏原有对象"与防污染原则冲突，另行设计）。
---

## 17. 诊断级别（诊断瘦身，21:5x 本地实现）

冷启动 57 s 里约 33 s 是"验收诊断"开销（逐对象/逐字段/逐命中日志 + 4 个 ICall 探针 + JSON 全量转储）。
新增三级开关，存 **MelonPreferences**：`CSTI_MiniLoader/DiagLevel = off | lean | full`（默认 full，用户说可以关再改默认）。

| 级别 | 行为 |
|---|---|
| `full` | 现状：JSON 全量转储、逐选项 `[EFFECT2]`、逐条 `[NAMEIDX] ✓ 按名解析`、`[WARPARR]/[RESOLVE]/[CHAIN]/[SET]` 痕迹、4 个探针、`[自检]` 全量键、`TraceLine` 写 csti_trace.log |
| `lean` | **跳过**探针/JSON 全量/逐条命中/痕迹写文件；保留 **一行汇总**：`[NAMEIDX] 单遍建索引完成 …`、`[EVENTCARD-SUM] <卡> json键=N 选项=N 产物链完整=N/N CardTags=N AllDrops=N`、`[INVARIANT] …`、`[NAMEIDX-SUM] 命中=… 未命中=…`、`[DIAGSUM] 级别=… 名字索引命中=… | 数组下潜样本=… | warp 痕迹=…`、`[STEP-T] <段> 耗时=…ms` |
| `off` | 只留错误 + `[STEP]`/`[自检]` 要点 |

**新增的量化手段**：`[STEP-T]` 分段耗时
```
[STEP-T] 0~1 探针 + LoadGameResource 耗时=…ms
[STEP-T] 2~3 arch 解析 + 编辑器对象 耗时=…ms
[STEP-T] 4 warp 全部 mod JSON（含名字索引首建） 耗时=…ms
[STEP] 9 done  总耗时=…ms  最后一段(5~9)=…ms
```
→ 下一次真机窗口即可精确知道 33 s 花在哪一段，而不是靠猜。

**实现要点**：守卫包在**干活之前**（不只是包 `MelonLogger`）—— 例如 `TraceLine` 在 lean/off 下直接 `return`（连字符串都不拼），
`DumpJson/DumpEffectArray/DumpNamedArray/DumpGenFields` 入口即 `return`，逐条 `[NAMEIDX] ✓` 改为计数器 + 末尾一行汇总。
全项目共 **27 处** `DiagFull/DiagLean` 守卫。