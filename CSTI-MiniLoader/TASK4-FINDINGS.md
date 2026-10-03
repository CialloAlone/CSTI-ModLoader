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
---

## 18. 去掉对 CstiICallFix 的硬依赖（22:23 本地完成，未推设备）

**为什么**：`CreateScriptableObjectViaShim` 原来是**反射调 `CstiICallFix.RealShims.CreateLike`**；
ICallFix 不在（或其回归导致启动崩溃）时它返回 null，回退的 `ScriptableObject.CreateInstance` 也失败
→ **204 个 mod 对象一个都建不出来**（真机实测 `增量 0`、`[WARP统计]` 缺失、STEP-T 4 warp=9ms）。

**改法**：`LoadArchMod.CreateScriptableObjectViaShim` 现在是**两条路径**，并各打计数：

| 路径 | 实现 | 日志 |
|---|---|---|
| ① `shim` | 反射 `CstiICallFix.RealShims.CreateLike`（历史主路径，保持兼容） | `[CREATE] 创建路径=shim <类型>` |
| ②a `fallback-clone` | **自带模板克隆**：`Diag.FindTemplatePtrByClassName` 从游戏注册表挑同类现成实例 → 已注册的
`UnityEngine.Object::Internal_CloneSingle` ICall 克隆 → 包成代理（**与 ① 同语义**，保留 Unity 原生状态最稳） | `[CREATE] 创建路径=fallback-clone <类型>（模板 0x… → 克隆 0x…）` |
| ②b `fallback-new` | 最后兜底：真实 il2cpp 类 `il2cpp_object_new` 建**空实例**（数据靠 NeutralizeClone + warp 填） | `[CREATE] 创建路径=fallback-new <类型>（il2cpp_object_new 0x…）` |

末尾汇总行：`[CREATE] 创建路径统计: shim=N fallback-clone=N fallback-new=N | UseOwnCreationFallback=… SkipShimCreation=…`

**新增 MelonPreferences**（`CSTI_MiniLoader/`）：
- `UseOwnCreationFallback`（默认 **true**）：关掉即回到"只依赖 ICallFix"的老行为；
- `SkipShimCreation`（默认 false）：置 true 可**强制只走自带兜底**，用于两条路径对照验收。

**待验判据**（等 Lead 放行后推）：ICallFix 移出 `Mods/` 跑一轮 → `增量 204/206`、`[EFFECT2]` 非空、
`[INVARIANT]` 0 变化；再把 ICallFix 放回 → 判据不变（两路径等价）。
---

## 19. 控制台看不到 mod 卡：真因与修法（22:38 真机验证）

**真因（与最初猜测不同）**：控制台（`CstiCheatConsoleMobile/Patches.cs`）在列表为空时会**自己调用游戏方法**
`__instance.FillCards()` 去填充 `CheatsManager.AllCards` ✓ —— 但那是"进档/开界面时只填一次"，
我们 206 个 mod 对象**是之后才注册的** ✗ → 列表里永远没有 mod 卡（命中 0/174 ✗）。
`GameLoad.Instance.DataBase.AllData` 它**根本不读** ✗（所以 `[ALLDATA] +206` 对可见性无用，但对别处仍有用，**保留** ✓）。

**修法**：`CheatListFix.cs`（反射实现，不依赖 interop 类型视图）
- 每约 4 秒（`HookFree.Tick`）观察 `CheatsManager.AllCards`：条目数 + 元素类型 + **mod 卡命中数/总数**；
- 命中 = 0 时**调用游戏自己的 `FillCards()`**（冷却 20 秒，幂等；游戏在进档时会重建该表，所以需要长期维护）；
- 两个开关（MelonPreferences）：`MaintainCheatLists`(true)、`CheatListsTriggerFill`(true)；
- **我们不自己写游戏列表** ✗ —— 让游戏自己按它的卡库重建 ✓，因此 `[INVARIANT]` 保持 0 变化 ✓。

**真机判据（22:36 Full / 22:38 Lean 两次都命中）**：
```
[CHEATLIST] CheatsManager.AllCards = 1906 项（元素类型=CardData）；mod 卡命中 = 0 / 174
[CHEATLIST] 触发 FillCards 后 CheatsManager.AllCards = 2080 项（元素类型=CardData）；mod 卡命中 = 174 / 174
[CHEATLIST] 触发 FillCards()（第 1 次）: 调用成功=True；mod 卡命中 0 → 174
```
（注：interop 编译期把 `AllCards` 看成 `List<InGameCardBase>` ✗，运行期反射实测元素类型是 **`CardData`** ✓ ——
所以"把 mod CardData 手工塞进列表"在类型上其实成立，但**没必要** ✓。）

**耗时对比（同一构建、同一包）**：
| 段 | Full | Lean |
|---|---|---|
| 0~1 探针+LoadGameResource | 179 ms | 93 ms |
| 2~3 arch 解析+编辑器对象 | 16,679 ms | 14,388 ms |
| **4 warp 全部 mod JSON（含名字索引首建）** | **41,067 ms** | **26,286 ms** |
| 5~9 | 437 ms | 280 ms |
| **总** | **58,364 ms** | **41,051 ms** |
→ lean 省 **17.3 秒（−30%）**；诊断已不再是瓶颈，剩下 26 秒是 **warp 本体**（692k 键 / 116k 写入）。
设备 prefs 现设为 `DiagLevel = "lean"`（`/storage/emulated/0/MelonLoader/<包名>/UserData/MelonPreferences.cfg`）。
---

## 20. GameSourceModify（改造游戏原有卡牌）：机制与修复（2026-10-03 00:31 本地完成，待 Lead 真机验收）

**为什么重要**：用户报"精灵能力不生效"（采摘柠檬草/芦荟/卡瓦/大叶仙茅/蜘蛛兰/椰子树/大树/热带杏仁树，
纤维缠细线、棕榈叶编织、泥堆→粘土）—— 这些**不是新卡**，而是把新动作/新掉落**追加到游戏原有卡上**，
走的正是 `GameSourceModify`（GSM）。而我们的 GSM 一直是 **inert**（`[GSM] … 保持inert=63` ✗）。

**文件结构（离线解包实证）**：`GameSourceModify/<组名>/<目标游戏卡 GUID>.json`，文件名 = 目标卡的 `UniqueID`。
- 分组：`windy 17 / bow 14 / combat_windy 8 / env 6 / locat 6 / tinder 6 / item 3 / accompany 1 / weather 1 / Modify_TemperaturePerceived_Desert 1` = **63**
- 被改字段：**`DismantleActions 48` / `CardInteractions 8` / `ExplorationResults 3` / `SpawningBlockedBy 3` / `SpoilageTime 1`**
- `WarpType`：`4=ADD`（追加，多数）/ `5=MODIFY`（先清空再填）
  ※ 枚举实际是 `NONE,COPY,CUSTOM,REFERENCE,**ADD=4**,MODIFY=5,ADD_REFERENCE=6` —— 早前笔记把 4 记成 MODIFY 是**错的**。

**根因（两处，缺一不可）**
1. **目标没解析**：PC 侧 `ModLoader.cs:1243` 用 `AllGUIDDict.TryGetValue(Guid, out var obj)` —— PC 的 AllGUIDDict 含**全部**卡牌；
   我们移植版的 AllGUIDDict **只登记 mod 对象**（游戏自带对象在 `UniqueIDScriptable.AllUniqueObjects`，
   当初又用 `typeof(UniqueIDScriptable)` 注册，被 `RegObj` 的子类判定挡掉）→ 63 条全部拿不到对象 ✗。
   **修**：`LoadArchMod` 生产端 + `LoadResources` 消费端都加"再查游戏注册表"的兜底 ✓。
2. **对象元素追加不了**：GSM 的 `*WarpData` 元素是**完整对象**（不是 GUID 字符串），
   而 `SetArrByWarpper<T>` 原来对每个元素一律 `ToString()` 当 GUID 解析 → 必然"解析到 0 项" ✗。
   **修**：新增分支 —— 元素是对象时 `Diag.NewElementOf(typeof(T))` 按元素类型新建实例 →
   `WarpFunc.JsonCommonWarpper` 递归填字段（含其内部 `*WarpData` 引用解析）→ 追加进数组 ✓。

**判据（日志可读，全部数字）**
```
[GSM] 目标已解析(游戏注册表): <类> / <名字> / GUID=…            （前 12 条）
[GSM] <类> <名字>(guid8): <字段> X→Y ✓ …                        （逐条，改前→改后）
[GSM] <类> <名字>(guid8): 无字段条目变化（…）                    （改到标量字段时的兜底提示）
[GSM] 有意修改汇总: 成功=N 无字段变化=M 失败=0 涉及对象=K + 清单
[INVARIANT] … ；已排除 GSM 有意修改 K 个 ✓ 尺寸与内容均完全不变
```
**"有意改动"与"意外污染"分离**：`Diag.IntentionalGuids` 登记被改对象的 GUID，
`CompareGameContainers` 按 `类|GUID` 跳过它们 ✓，清单单独打印 ✓；其余对象仍要求 0 变化 ✓。
**健壮性**：消费者本来就 per-item `try/catch` ✓，单条失败只记日志继续 ✓；新建实例失败另打
`[ARR] 无法为对象元素新建实例: … 元素类型=T` ✓。
---

## 21. GSM 第二轮：两处安全问题的定位与修复（2026-10-03 00:38 本地完成，待真机复查）

真机第一轮结果：`成功=62 无字段变化=1 失败=0 涉及对象=47`、`字段存在=False` 出现 0 次（Retype 修复生效 ✓），
但暴露两个安全问题：

### 问题 1：`[INVARIANT] 已排除 GSM 有意修改 0 个` —— **时序**问题
`CompareGameContainers(invSnap, "warp 后")` 原来在 **STEP 4 之后、STEP 5（GSM 应用）之前**执行，
此时 `IntentionalGuids` 还是空的 → 排除数必然 0（不是登记缺失、也不是 GUID 对不上）。
- 键核对：快照键 = `RegistryByClass` 的 `kv.Key` = 游戏注册表 **GUID** ✓；`IntentionalGuids` 存的也是 GSM 目标 GUID ✓ → 同源可匹配 ✓。
- 登记时机：`LogGsmEntry` 在**每条应用后立刻**登记 ✓（非汇总时）。
- **修**：GSM 之后再跑一次判据 → `[INVARIANT] warp+GSM 后 …；GSM 有意修改目标总数=47，其中落在本次采样内=K（已排除）`。
  采样是"每类 ≤20、共 76 个"，47 个目标不可能全在采样内 → **K < 47 是正常的**，两个数一起打出来才能判断"漏采"还是"排除没生效"。

### 问题 2：`6 项[…] → 6 项[DismantleCardAction × 6]` —— 打印副作用（已加"无法含糊"的判定证据）
- **打印副作用来源**：ADD 路径会**重建数组**（元素指针不变），读回时元素代理可能退化成基类/`Il2CppSystem.Object` → `ActionName` 取不到 → 回退成类名。
- **修**：`ElemName` / `ElementDetails` 取值前先 `Diag.Retype(e)`；容器字段的改前/改后比较改用**逐元素明细**
  （条数 + `ptr` + `ActionName.DefaultText` + `DaytimeCost` + `ProducedCards` + `RequiredCardsOnBoard` + `RequiredTagsOnBoard`）。
- **判定口径**（Lead 指定）：同一元素改前/改后**指针与字段值完全一致 → 只是打印问题**；
  任一不同 → 真损坏，须改为"只追加、绝不重建"。（当前 `ADD` 路径本就是 `原元素 + 新增`，不清空；只有 `MODIFY(5)` 会先清 —— 63 条里 17 条是 MODIFY，语义即"替换"。）
---

## 22. ⛔ 教训：绝不按"基址+偏移"直写非托管内存（2026-10-03 启动期 SIGSEGV）

**做了什么**：为把 GSM 新增元素的**值类型字段**（`DaytimeCost` 是 int、`ActionName` 是内联 `LocalizedString`）
填上，warp 里那条"故意跳过内联值类型"的安全跳过被我改成了**直写内存**：
`Marshal.Write*(objPtr + tuple.fOffset, …)`，字符串用 `il2cpp_string_new` + 写屏障，结构递归写。

**结果（真机）**：日志连续 10+ 条 `[INL] 内联值类型已直写: CardAction.ActionName = {空值}`，
**随后进程直接消失** —— 没有 `后置复读`、没有 `有意修改汇总`、没有 `[INVARIANT]`、没有 `增量 206`。
`ActionName` 写的是**空值**（JSON 没解出来/偏移不对），且**把内存写坏 → SIGSEGV**。

**结论（永久生效）**：
1. 原始实现里"跳过内联值类型"不是保守，而是**必须** —— 这条路走不通；
2. 值类型字段只允许走**托管属性 setter**（`prop.SetValue`，运行时保证类型/GC 安全），
   且默认**不开**（`CSTI_MiniLoader/GSM.InlineWrite=false`），失败就跳过并记 `[GSM] ⚠ 新增元素字段跳过`；
3. 加载期任何"改造游戏对象"的整段代码都必须包 try/catch，**一条异常不许掀翻进程**；
4. 提供 `CSTI_MiniLoader/GSM.Apply`（默认 true）作为"完全不碰游戏对象"的一键回退；
5. 值类型内容的另一条安全路：直接调已确认可用的 ICall
   `UnityEngine.JsonUtility::FromJsonInternal(json, objPtr, il2cpp_type_get_object(il2cpp_class_get_type(cls)))`
   —— **显式传真实类型**，绕开托管版 `FromJsonOverwrite` 用包装类型推断（`Cast<Il2CppSystem.Object>` 会让 Unity 看到 `System.Object`，一个字段都写不进）。

**审计口径**（每次改完都要跑）：全项目 `.cs` 搜 `Marshal.Write|WriteInlineValue|il2cpp_field_set_value` → 必须 **0 处**。
---

## 23. GameSourceModify（精灵能力）五轮修复记 —— 从"全 inert"到"能力可用"

**用户诉求**：精灵应能采摘柠檬草/芦荟/卡瓦/椰子树…、纤维缠细线、棕榈叶编织、泥堆→粘土。
这些**不是新卡**，而是把新动作/掉落**追加到游戏原有卡牌**上 —— 机制是 `GameSourceModify`（63 条）。

| 轮次 | 真机症状 | 根因 | 修法 |
|---|---|---|---|
| 1 | `本会改到游戏对象=0 保持inert=63` | 我们只查 `AllGUIDDict`（**只含 mod 对象**）；游戏自带对象在 `UniqueIDScriptable.AllUniqueObjects` | 生产端+消费端都加"再查游戏注册表"兜底 |
| 2 | 目标解析成功但 **0 条写入**（无告警） | 目标是游戏对象 → 包成基类 `UniqueIDScriptable` → `GetOrGen(基类)` 无 `DismantleActions` → 每键 `continue` 静默跳过 | `WarpFunc` 入口 + 消费端统一 `Diag.Retype(obj)` |
| 3 | 追加生效但**内容全空**（`ActionName="-"`） | ① **改错了分支**：`CommonSet` 对"对象元素数组"分派到 **`SetArrNoWarpper`**（不是 `SetArrByWarpper`）② warp 明确**跳过内联值类型** → `DaytimeCost`/`ActionName` 写不进 ③ 嵌套对象字段为 `null` 被跳过 | 改 `SetArrNoWarpper`：新建元素先 `Diag.DeserializeElement`（**无条件走 `JsonUtility::FromJsonInternal` ICall**，显式传真实类型）再 warp；嵌套对象 null 时先建出来挂上 |
| 4 | **启动期 SIGSEGV**（进程消失） | 我一度用"基址+字段偏移"**直写非托管内存**填值类型 → 写坏内存 | **整条撤销**（永久），值类型只允许托管属性 setter，且默认关（`GSM_InlineWrite=false`）；GSM 整段 try/catch；新增 `GSM_Apply` 一键回退 |
| 5 | 能力名进游戏 ✓（`让风精灵摘点叶子。`）但 **7 个原元素引用被替换** | 新建元素反序列化已填好普通容器，紧接着的**普通 warp 又按 ADD 重建**它们 | 新建元素改用 `JsonWarpKeysOnly`（只处理 `*WarpType`＝只解 `*WarpData` 引用），MODIFY 就地改、绝不清空/不换引用 |
| 6 | 残留判据是否可信 | 托管索引器读原元素抛异常被 `catch {}` 吞掉 → 可能误报 | 判据改**裸读原数组槽位**（与写回同一套偏移）+ ⚠ 行加 `阶段=` / `调用来源=`（前三帧栈）→ 真机确认是**真丢引用**（`读取失败=0`） |
| 7 | 7 处仍丢 | 反射/包装器（`RuntimeMethodInfo.InternalInvoke`）绕过 `JsonCommonWarpper` 主循环的 `WarpKeysOnly` 守卫 | 守卫**下沉到 4 个数组/List 函数内部**（`WarpKeysOnly` 为真时只允许空容器被填）→ 真机 `拦下的非空重建=0`，**守卫未触发**，残留接受 |

**最终真机判据**：`元素反序列化 成功=200 失败=0`、`FromJsonInternal 成功=200 失败=0`、
中文明能力名已在游戏内（隐匿气息撤退 / 紧紧握在手中(点火) / 让风精灵摘点叶子。…）、
`[INVARIANT] 0 变化 ✓`、`失败=0 ✓`、`增量 206 ✓`、未崩 ✓。

### 已知残留（**未解决**，按 Lead 决定不再深挖）

最终真机（第 6/7 版）：`引用保留核对 372039/372046` —— **7 个嵌套 ADD 容器的元素引用被重建**，集中在
`CardsDropCollection.CardDropChanceModifiers`(1→1) / `GameStat.TimeOfDayMods`(2→2) / `ExtraDurabilityChange.NOTAffectedThings`(3→4)，
`模式=ADD`，`阶段=新建元素追加(Array)`，`调用来源=SetArrNoWarpper<…> ← RuntimeMethodInfo.InternalInvoke`。

**为什么没修掉（如实记录）**：
- 第 6 轮把判据从托管索引器改为**裸读原数组槽位**（排除"读取异常被 catch 吞掉 → 误报"），真机给的是 `读取失败=0`
  → **确认是真丢引用**，不是判据假象；
- 第 7 轮把守卫下沉到 `SetArrByWarpper` / `SetArrNoWarpper` / `SetLiByWarpper` / `SetLiNoWarpper` 函数内部
  （"`WarpKeysOnly` 为真时只允许空容器被填、绝不允许非空容器被重建"），覆盖反射/生成包装器那条调用链；
  真机结果 `只解引用阶段拦下的非空重建=0` → **守卫根本没被触发** ✗ → 这些重建来自**另一个阶段**，
  继续追必须动"值类型写入 / 数组写回"两个**已知危险区**（本项目已因此崩溃两次）。
- **影响评估**：被重建的元素内容**取自同一份 JSON**，用户可见能力不受影响（中文明能力名已在游戏内生效）；
  风险/收益比 7 : 372046 → **接受为已知残留**。

### 两条永久教训
1. ⛔ **绝不按"基址 + 字段偏移"直写非托管内存**（见 §22）：真机启动期 SIGSEGV。值类型字段只能走托管属性 setter，
   且默认关闭；加载期改造游戏对象的整条代码必须包 try/catch。
2. ⚠ **"引用丢失"的判据不能只看托管索引器**：`existingArr[i]` 抛异常会被 `catch {}` 吞掉 → 计数变 0 →
   伪装成"引用被替换"。要么裸读槽位（与写回同一套偏移），要么把读取失败单独计数（`读取失败=`）后再下结论。

---

## 24. ★ `prop=null` ×95,180 的真根因与通解（2026-10-03 11:38 真机验收通过）

> 结论先行：交接文档 §2 的计划（"用官方字段 API 现造 boxed 结构逐子字段写"）**方向对、但前提错了**。
> 用 `System.Reflection.Metadata` 直接读**设备上真正加载的** `MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll`
> 之后，真因是：**Il2CppInterop 对 il2cpp 值类型有两种生成形态**，而我们的访问层只会用属性。

### 24.1 实证：两种生成形态（离线证据，可复现）

工具：`temp/metadump`（自写，`dotnet run -- <dll> <类型名> [@成员过滤]`，只读 System.Reflection.Metadata）。

| il2cpp 值类型 | interop 里的形态 | 成员 |
|---|---|---|
| `LocalizedString`、`DurabilityConditions`、`CardInteractionTrigger`（**含引用**） | 基类 = `Il2CppSystem.ValueType` 的**类** | **属性**（可读可写） |
| `DurabilitiesConditions`、`DurabilityWeightValue`、`EncounterVariable`、`EnemySkillModifier`、`EnemyWoundModifier`、`EnemyWoundBasedWeightModifier`、`LightSourceSettings`、`SimpleHitProbabilityModifier`（**blittable**） | 基类 = `System.ValueType` 的 **C# struct**（`ExplicitLayout`） | **public 字段，没有属性** |

- 设备侧 `UnityEngine.CoreModule.dll` 里 `Vector2` = `public float x/y`（字段）、`Vector2Int` = `public int m_X/m_Y` + 属性 `x/y`、`Color` = `public float r/g/b/a`。
- 宿主端（`ExtraDurabilityChange.RequiredDurabilities` / `GeneralCondition.RequiredDurabilityRanges` /
  `AmbienceSettings.AmbienceConditions` / `EnemyAction.EnemySkillClashModifier` / `CardData.LightSource` /
  `CardsDropCollection.DurabilitiesDropChanceModifier` / `PlayerEncounterVariable.AddedValue`）**都是可写属性**
  → 外层 `prop.GetValue` 拿到的是**托管结构副本**，`prop.SetValue` 整块写回是有效的。

### 24.2 因此 `prop=null` 是**误报**，真正缺的是两件事

1. **成员访问只有属性一路** → blittable 结构（以及它们内部的 `Vector2/Color/…`）恒 `prop=null` → 95,180 行刷屏 + **这一类字段从来没写进去**。
2. **主 warp 没有标量通道** → 这些结构的 JSON 键大量是标量（`{"SpecialNRange":{"x":0,"y":100}}`），
   主循环只认 `*WarpData`/对象/容器 → 标量静默跳过 → 即使结构能拿到，叶子也永远是默认值。

### 24.3 修法（三处，全部零偏移、零裸内存）

| 位置 | 改动 |
|---|---|
| `Diag.cs` | 新增**托管成员访问层** `FindMember/TryReadMember/TryWriteMember`（**属性优先 → public 字段兜底**，带缓存、可赋值性检查、绝不隐式强转）；`TrySetStructViaProxy` 改用它，失败一律 `NoteInlineIssue` **去重计数**（★ 删掉原来逐条的 `MelonLogger.Warning`，30MB 日志的元凶） |
| `WarpFunc.cs` | 新增 `ManagedStructWarp`：**不是 `Il2CppObjectBase` 的托管副本**（= boxed 结构）走"托管成员"通道，对象键递归、标量键精确转换（`Diag.TryConvertScalar`）；`StatLine` 增加 `托管结构写入=N(字段=… 属性=…)` |
| `MainGenTools.cs` | `CommonSetFld` 的"源→目标"分支修正**语义错位**：`data` 类型与目标成员匹配时**直接写回**（去共享新实例 / 嵌套值类型子对象 warp 完写回），不匹配才回退旧的"同名成员读→写"。旧代码去源对象上找**目标字段名**（`LocalizedString.CardName`）→ 必然 null → 真机 68 类 / 4 万+ 次失败**且数据没写回** |

**顺带修掉的一个真 bug**：`Diag.TryConvertScalar` 原来用 `v.String` 取标量文本，而 mod JSON 走的是
`MapperItem`（modArch 二进制），其 `String` 对**非字符串恒为空串**（文本只在 `ToJson()` 里）→
56 万次 `Vector2.x/y` 转换 FormatException。新增 `Diag.RawScalarText` 同时覆盖
`JsonKVProvider`（LitJson）与 `MapperItem` 两种实现（首轮真机实测抓到的，见 §24.5 第 1 轮）。

### 24.4 验收判据（真机 `Logs/26-10-3_11-38-8.log`，420 KB / 2,532 行）

| 判据 | 交接文档要求 | 实测 | 旧代码 |
|---|---|---|---|
| `[BUILD]` 身份行 | 必须证明"跑的=改的" | `自身sha前16=60693F5804338CA7` = 本地产物 SHA256 前 16；构建时间 11:38:02；源码路径正确 | 旧轮 SHA 对得上但读错日志 |
| `prop=null` | 归零 | **0** | 95,180 |
| `[INL] 内联结构写回汇总` | 唯一问题很小 | `成功=429200 失败=0 唯一问题=0` | `失败=95180 唯一问题=68` |
| `[INL]` 明细行 | 无/极少 | **0 行** | 68 行 × 数万次 |
| 字段路径被真正用到 | `字段API > 0` | `托管结构写入=891675(字段=850907 属性=40768)` | 0（根本没有字段路径） |
| 写回是否真进真对象 | — | `读回校验=292939 读回不一致=0`（真对象读回逐成员比对） | 无此判据 |
| 语义错位分支 | — | `直接写回=38297` | 全部失败 |
| 底线 | 不崩 / `[INVARIANT]` 0 变化 / `增量 206` / 加载成功 | 全部 ✔（两次对比 尺寸/内容/丢失=0；`增量 206`；`[STEP] 9 done 总耗时=98263ms`） | — |
| `[MODCARD] 通用汇总` | 全空=0 | ✔ 28 卡 / 106 交互项 / 全空=0 | ✔ |
| `[GSM] 有意修改汇总` | 成功=61 无变化=2 失败=0 | ✔ 完全一致 | ✔ |
| 日志体积 | 回落到几百 KB | **420 KB**（−98.6%） | 30.9 MB |
| `跳过(无字段)` | — | 12 | 562,023 |

### 24.5 过程记录（两轮真机，含一次自身缺陷）

1. **第一轮**（11:31，`Logs/26-10-3_11-31-15.log`）：`prop=null` 已归零、`失败=0`，但
   `唯一问题=15` 全是 `Vector2.x|托管结构键未处理（标量类型不支持 目标类型=Single）` ×56 万
   → 抓到上面那个 `MapperItem.String` 空串缺陷（**若不复跑就会把"叶子全是 0"当成成功**）。
2. **第二轮**（11:38）：全绿，见 §24.4。
3. 交接文档 §2 里"读错日志文件"的教训继续有效：**必须读 `MelonLoader/Logs/<最新>.log`**。

### 24.6 与交接文档计划的差异（如实记录）

- 计划要求"新增 `il2cpp_object_new`+`il2cpp_field_set_value` 逐子字段写"的**官方字段 API**助手。
  实测证明**不需要**：这些结构在托管侧就是货真价实的 C# struct（`ExplicitLayout`），
  在**托管副本**上按 public 字段写、再经 interop 属性 setter 整块写回，全程只碰托管内存，
  比手写 boxed 结构 + 逐子字段 native 写**更安全**（避开 §22 那类崩溃风险），且已被 29 万次读回校验证明有效。
- `CopyStructFieldViaFieldApi`（§3 已实现的 object→object 官方字段 API 拷贝）保留为回退通道，本轮未被触发
  （`官方字段API=0`），计数保留在汇总行里可见。
- 结论：**判据以"字段路径真的被用上 + 读回一致"为准**（`托管结构写入(字段=850907)` + `读回不一致=0`），
  而不是"某个特定 API 被调用"。这一点写进文档，避免下次又按错误前提去补实现。

### 24.7 用户级影响

- 之前这一类字段（耐久区间 / 照明参数 / 敌人技能与创伤修正 / 命中概率修正 / 掉落权重区间 …
  以及**任何 mod 卡 JSON 里出现的这些结构**）**从未被写入**；GSM 改造游戏对象时又没有 JsonUtility 兜底
  → 这些值一直是默认值。本轮起按 JSON 真实写入，且**读回校验 0 不一致**。
- 仍需用户复测的：拖拽交互（纤维/棕榈叶/泥堆 → 风精灵）。数据层判据已全绿
  （`交互项=106，TriggerCards/TriggerTags 全空=0`），但**手感类结论只能由用户在游戏里给**。