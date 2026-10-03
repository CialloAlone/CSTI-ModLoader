using System;
using CSTI_MiniLoader.LoadUtil;
using MelonLoader;
using Il2CppSystem.Collections;
using Il2CppSystem.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Array = Il2CppSystem.Array;
using Object = Il2CppSystem.Object;

namespace CSTI_MiniLoader.WarpperClassGen;

public static class WarpFunc
{
    // ===== 诊断计数 =====
    // JsonCommonWarpper 每个键都用一个空 catch 吞异常、字段查不到就 continue，
    // 所以外部永远看不到「什么都没写进去」。这里把内部真实情况记下来。
    public static int StatKeys, StatSkipped, StatSet, StatEx, StatEmptyType;
    /// <summary>回归修复：被跳过的 List 原地改写次数（克隆体与模板共享 List，写了会污染游戏数据）。</summary>
    public static int StatListSkipped;
    /// <summary>回归修复：被跳过的嵌套子对象原地改写次数（与游戏资产共享实例，写了会污染游戏数据）。</summary>
    public static int StatObjSkipped;
    /// <summary>诊断：只跟踪前 N 次嵌套对象 warp（原生崩溃前定位用）。</summary>
    public static int ObjTrace;
    private static readonly System.Collections.Generic.List<string> StatExSample = new();
    private static readonly System.Collections.Generic.List<string> StatSkipSample = new();
    /// <summary>「字段不在 gen 表」的采样（无条件记录，上限 30）—— 空字段问题的直接证据。</summary>
    // [2026-10-03] 逐实例列表 → **去重 + 计数**（真机 95,180 行刷屏就是这里：删 cap 后它无上限增长 ✗）。
    //   用户口径：按条件筛（去重键 + ×次数），不是 cap；失败/跳过必须可见但不刷屏。
    public static readonly System.Collections.Generic.Dictionary<string, int> SkipKeyCounts = new();
    /// <summary>内联值类型直写的日志条数上限（避免刷屏）。</summary>
    public static int InlineLogged;

    /// <summary>
    /// "只解引用"模式：warp 只处理 `*WarpType`（即 `*WarpData` 的引用解析），跳过普通容器/标量。
    /// 专供**新建元素**：那些字段已由 `Diag.DeserializeElement`（ICall）连原引用一起填好，
    /// 再走普通 warp 会按 ADD 重建数组 → 原元素引用丢失（真机 7 处）。
    /// </summary>

    /// <summary>同上，但保证调用结束后恢复原值（可嵌套安全）。</summary>
    /// <summary>嵌套对象"新建并挂上"的次数（空动作修复②的判据）。</summary>
    public static int NestedCreated;

    public static void ResetStats()
    {
        StatKeys = StatSkipped = StatSet = StatEx = StatEmptyType = StatListSkipped = StatObjSkipped = 0;
        StatExSample.Clear();
        StatSkipSample.Clear();
    }

    public static string StatLine()
    {
        return "warp: 键=" + StatKeys + " 写入=" + StatSet + " 跳过(无字段)=" + StatSkipped
               + " 内部异常=" + StatEx + " 空gen表类型=" + StatEmptyType
               + " List原地改已跳过=" + StatListSkipped + " 嵌套对象原地改已跳过=" + StatObjSkipped
               + " 托管结构写入=" + ManagedWrites + "(字段=" + ManagedFieldWrites + " 属性=" + ManagedPropWrites + ")";
    }

    public static void DumpSamples()
    {
        foreach (var s in StatSkipSample) MelonLogger.Warning("[WARP跳过] " + s);
        foreach (var s in StatExSample) MelonLogger.Warning("[WARP内部异常] " + s);
    }

    public static void JsonCommonRefWarpper(object obj, KVProvider data, string fieldName,
        WarpType warpType = WarpType.REFERENCE)
    {
        MainGenTools.CommonSet((Il2CppObjectBase)obj, fieldName, data, warpType);
    }

    public static void JsonCommonWarpper(object? obj, KVProvider json)
    {
        if (!json.IsObject) return;
        if (obj == null) return;
        // ★★ [A-3 快速通道] 纯标量类型（Vector2/LocalizedString/OptionalRangeValue…）直接托管标量写回，
        //    跳过完整 warp 机制（真机 40 万次递归耗在这里 ✗）。行为等价、零信息丢失 ✓，条件不满足即走原路径 ✓。
        if (Diag.TryPureScalarFastPath(obj, json)) return;

        // ★★ [2026-10-03 GSM 关键修复] 目标对象常常是**游戏自带对象**，Il2CppInterop 把注册表里的
        //    游戏对象统一包成基类 `UniqueIDScriptable` → `obj.GetType()` 是基类 →
        //    `MainGen.GetOrGen(基类)` 的表里没有 `DismantleActions`/`CardInteractions`
        //    → 每个键都走 `genInfos.TryGetValue(...) == false → continue`（**静默跳过**）
        //    → 63 条 GSM「目标解析成功但一个字都没写进去」（真机实测：成功=0 无变化=63）。
        //    按 il2cpp 真实类名重建代理后，字段表才是 CardData 的完整（含继承）表。
        var retyped = Diag.Retype(obj);
        if (retyped != null) obj = retyped;

        // ★★ [2026-10-03 第 10 轮 · 真根因修复] **纯托管值类型副本（boxed struct）走"托管成员"通道**。
        //    用 System.Reflection.Metadata 直接读设备上的 Il2CppAssemblies/Assembly-CSharp.dll 实证：
        //    Il2CppInterop 对 il2cpp 值类型有两种生成形态 ——
        //      · 含引用的（LocalizedString / CardInteractionTrigger）→ `Il2CppSystem.ValueType` 派生的**类 + 属性**；
        //      · **blittable 的**（DurabilitiesConditions / DurabilityWeightValue / EncounterVariable /
        //        EnemySkillModifier / LightSourceSettings / Vector2 / Color …）→ `ExplicitLayout` 的
        //        **C# struct + public 字段**（没有属性）。
        //    而这类结构的 JSON 键**大量是标量**（`{"SpecialNRange":{"x":0,"y":100}}`），主 warp 的每条分支
        //    都只认 `*WarpData`/对象/容器 ⇒ 标量一律静默跳过 ⇒ 结构字段永远保持默认值
        //    （真机 `prop=null` 95,180 行 / 30MB 日志的实质就是这一类）。
        //    判据严格限定为"**不是** Il2CppObjectBase 的托管副本"：真 il2cpp 对象仍走原路径，行为不变。
        if (MiniLoader.StructMemberFix && obj is not Il2CppObjectBase)
        {
            ManagedStructWarp(obj, json);
            return;
        }

        var objType = obj.GetType();
        var genInfos = MainGen.GetOrGen(objType);
        if (genInfos.Count == 0)
        {
            StatEmptyType++;
            StatSkipSample.Add("gen 表为空（该类型没有任何 NativeFieldInfoPtr 字段）: " + objType.FullName);
        }

        // ★★ [2026-10-03 两遍处理] 引用类字段的最终写入顺序：**先普通键（含 Unity 占位）→ 后 `*WarpData` 解析值**。
        //    真机证据：`CardImage` 键被遍历 ✓、`CommonSet` 被调用 ✓、无跳过/异常 ✓，但最终仍 null ✗，
        //    同形态的 `CardBackground` 成功 ✓ ⇒ 最可能是**占位/普通键在收尾阶段又写了空值** ✗。
        //    两遍处理让"解析出来的真实引用"**最后落盘**，覆盖 `CardImage`/`CardTags`/`WhenCreatedSounds`/
        //    `GivenCardChanges.TransformInto` 等同症状字段（通用规则，不按字段名/卡名特判 ✓）。
        var __keys = new System.Collections.Generic.List<string>();
        foreach (var __k in json.Keys) __keys.Add(__k);
        var __total = __keys.Count;
        var __i = 0; var __last = "-";
        for (var __pass = 0; __pass < 2; __pass++)
        foreach (var key in __keys)
        {
            var __isWarpKey = key.EndsWith("WarpData") || key.EndsWith("WarpType");
            if ((__pass == 0) == __isWarpKey) continue;   // pass0=普通键；pass1=*WarpData/*WarpType
            __i++; __last = key;
            StatKeys++;
            try
            {
                var keyData = json[key];

                // ★ [2026-10-03 第 5 轮] "只解引用"模式：新建元素已经用 `DeserializeElement`（ICall 反序列化）
                //    把**普通容器/标量**（`CardDropChanceModifiers`/`TimeOfDayMods`/`NOTAffectedThings`…）
                //    连同其**原始引用**一起填好了；若紧接着再走一遍普通 warp，ADD 逻辑会**重建这些数组** →
                //    原元素引用丢失（真机实测 7 处）。所以这一步只处理 `*WarpType`（即 `*WarpData` 的解引用），
                //    其它键一律不碰。开关只在"新建元素"路径上打开，正常 mod 对象不受影响。
                // ★ [2026-10-03 第 8 轮 · 用户级 bug 修复] "只解引用"模式**不能只跳普通标量**：
                //    用户报"纤维/蛇草拖不到精灵身上"，真因就是交互判定字段藏在**嵌套对象**里：
                //      `CompatibleCards: { TriggerCards: [], TriggerCardsWarpData: ["748f5b60…"],
                //                          TriggerCardsWarpType: 3 }`
                //    `CompatibleCards` 是普通对象键 → 被这里的 keys-only 守卫跳过 → 它的**内层**
                //    `TriggerCardsWarpData` 永远不解引用 → `TriggerCards` 为空 → 拖拽永不匹配（用户看到的症状）。
                //    所以：keys-only 下**放行对象/数组键**（只做"下潜解引用"，不重建容器），只跳真正的标量。

                if (key.EndsWith("WarpType"))
                {
                    if (!keyData.IsInt || !json.ContainsKey(key.Substring(0, key.Length - 8) + "WarpData"))
                        continue;
                    var fieldName = key.Substring(0, key.Length - 8);
                    if (!genInfos.TryGetValue(fieldName, out var tuple))
                    {
                        StatSkipped++;
                        Diag.NoteSkipKey(objType.Name + "." + fieldName + "|gen表无此字段");
                        Diag.NoteWarpKey(fieldName + "WarpData", objType.Name, "字符串", "跳过(gen表无此字段)", __i, __total);
                        StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
                        continue;
                    }
                    var fieldWarpData = json[fieldName + "WarpData"];
                    // ★ [WARP-KEY] 键级判据：处理后记"写入"；对 CardImage/CardBackground 再做**写入后复读**
                    //   （成功只累计计数 ✓、跳过/异常按 键|结果 去重首次一行 ✓、无 cap ✓）
                    try
                    {
                        var shape = fieldWarpData.IsArray ? ("数组(" + fieldWarpData.Count + ")")
                            : fieldWarpData.IsObject ? "对象" : fieldWarpData.IsString ? "字符串" : "其它";
                        MainGenTools.CommonSet((Il2CppObjectBase)obj, fieldName, fieldWarpData, (WarpType)keyData.Int);
                        Diag.NoteWarpKey(key, objType.Name, shape, "写入", __i, __total);
                        if (fieldName is "CardImage" or "CardBackground")
                            Diag.NoteWarpRefReadback(obj, fieldName, key, fieldWarpData);
                    }
                    catch (Exception wke)
                    {
                        Diag.NoteWarpKey(key, objType.Name, "?", "异常(" + wke.GetType().Name + " " + wke.Message + ")", __i, __total);
                    }

                    StatSet++;
                }
                else if (key.EndsWith("WarpData"))
                    continue;
                else
                {
                    if (keyData.IsObject)
                    {
                        var fieldName = key;
                        if (MiniLoader.DiagFull && fieldName is "DroppedCard" or "ActionName")
                            MelonLogger.Msg("[CHAIN] 对象字段 " + objType.Name + "." + fieldName);
                        if (!genInfos.TryGetValue(fieldName, out var tuple))
                        {
                            StatSkipped++;
                            if (MiniLoader.DiagFull && fieldName is "DroppedCard" or "ActionName" or "ProducedCards")
                                MelonLogger.Msg("[CHAIN]   ↳ 字段不在 gen 表: " + objType.Name + "." + fieldName);
                            Diag.NoteSkipKey(objType.Name + "." + fieldName + "|gen表无此字段");
                            StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
                            continue;
                        }
                        if (tuple.fldType.IsSubclassOf(typeof(UnityEngine.Object)))
                            continue;
                        // ⛔ [2026-10-03 已撤销] 曾经"按 基址+字段偏移 直写非托管内存"来填值类型字段，
                        //    真机结果：启动期 **SIGSEGV**（把内存写坏，进程直接消失，日志停在 [INL] 那几行）。
                        //    该路径整条删除，**永不恢复**。值类型只允许走"托管属性 setter"这条安全通道：
                        //    默认连它也**不用**（`CSTI_MiniLoader/GSM.InlineWrite=false`），
                        //    打开后也只是 `prop.SetValue`（运行时保证类型/GC 安全），失败就跳过并记日志。
                        if (tuple.isValueType)
                        {
                            // ★★ [2026-10-03 第 9 轮 · 用户级 bug 修复] 内联结构**不再跳过**：
                            //    `prop.GetValue` 取**托管代理** → 在代理上递归 warp
                            //    （`TriggerCardsWarpData` 因此被解引用进 `TriggerCards`）→
                            //    `prop.SetValue` 把**整块结构**写回字段。
                            //    全程托管属性 setter：**不用字段偏移、不做 memcpy、不写裸内存**。
                            //    真机证据：mod 卡 `Windy.CardInteractions` 37 项 TriggerCards/TriggerTags 全空
                            //    → 用户"纤维/蛇草拖不到精灵身上"。
                               // ★★ [跳过结论缓存] 同一 (宿主类型,字段) 已判定写不进 → 直接跳过，不再重解
                               //    （真机 95,192 次跳过里绝大多数是同一道题；首次仍逐条打印、唯一原因全量保留、汇总计数不变 ✓）
                               if (Diag.StructWriteKnownImpossible(objType.Name, fieldName))
                               {
                                   StatSkipped++;
                                   Diag.StructSkipCacheHits++;   // ★ 只计数，**一行都不打**（I/O 才是大头）
                               }
                               else if (MiniLoader.StructSetterFix && Diag.TrySetStructViaProxy(obj, fieldName, keyData))
                            {
                                StatSet++;
                            }
                            else if (MiniLoader.GsmInlineWrite && Diag.TrySetManagedField(obj, fieldName, keyData))
                            {
                                StatSet++;
                            }
                            else
                            {
                                StatSkipped++;
                                Diag.NoteSkipKey(objType.Name + "." + fieldName + "|内联值类型代理 setter 不可用");
                        Diag.MarkStructWriteImpossible(objType.Name, fieldName);
                            }

                            continue;
                        }

                        // [2026-10-02 重新启用] 嵌套子对象 warp：
                        // `Diag.NeutralizeClone` 已把克隆体的嵌套子对象换成 **mod 私有副本**
                        //（数组等长 + 元素同类型新实例 + 递归），因此这里的写入只落在 mod 自己那份上，
                        // 不会再污染游戏资产（`[INVARIANT]` 判据继续保持 0 变化）。
                        // ★ 这条正是事件选项能否产出的关键：DismantleActions[i].ProducedCards 是 WarpData 引用，
                        //   只有嵌套 warp 才会把它解析成真实的 CardsDropCollection。
                        var subObj = MainGenTools.CommonGet((Il2CppObjectBase)obj, fieldName);
                        if (subObj == null)
                        {
                            // ★ [2026-10-03 空动作修复②] 新建的元素里这些嵌套对象字段都是 **null**
                            //（例如 `ProducedCards` → `CardsDropCollection`）。原来是直接跳过 →
                            // 追加出来的动作"有壳无肉"。现在按字段类型**先建出来**、挂上去、再递归 warp。
                            var createdSub = Diag.NewElementOf(tuple.fldType);
                            if (createdSub != null &&
                                Diag.SetObjectField(obj, fieldName, createdSub))
                            {
                                NestedCreated++;   // [条件筛选] 成功只计数（逐条打会刷屏）

                                subObj = createdSub;
                            }
                            else
                            {
                                StatSkipped++;
                                Diag.NoteSkipKey(objType.Name + "." + fieldName + "|嵌套对象为 null 且新建失败");
                                continue;
                            }
                        }

                        JsonCommonWarpper(subObj, keyData);
                        MainGenTools.CommonSetFld(obj, fieldName, subObj);
                        StatSet++;
                    }
                    else if (keyData.IsArray)
                    {
                        var fieldName = key;
                        if (MiniLoader.DiagFull && fieldName is "DismantleActions" or "ProducedCards" or "DroppedCards")
                            MelonLogger.Msg("[CHAIN] " + objType.Name + "." + fieldName + " 数组: json元素="
                                            + keyData.Count + " 首元素是对象="
                                            + (keyData.Count > 0 && keyData[0].IsObject));
                        if (!genInfos.TryGetValue(fieldName, out var tuple))
                        {
                            StatSkipped++;
                            StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
                            continue;
                        }

                        for (var i = 0; i < keyData.Count; i++)
                        {
                            if (keyData[i].IsObject)
                            {
                                // ★ [2026-10-03 第 8 轮] 只解引用模式：**下潜到既有元素**解它们的内部引用，
                                //    绝不走 CommonSet/SetArrNoWarpper（那会按 ADD 重建数组 → 丢原引用）。
                                //    例：`CompatibleCards.TriggerCards` 空 → `TriggerCardsWarpData` 需解引用；
                                //    以及 `ReceivingCardChanges[].TransformIntoWarpData` 这类元素内部引用。

                                if (tuple.fldType.IsGenericType &&
                                    tuple.fldType.GetGenericTypeDefinition() == typeof(List<>))
                                {
                                    // [2026-10-02 重新启用] 列表元素 warp：List 实例已被 NeutralizeClone
                                    // 换成 mod 私有的新列表，原地改只影响自己那份。
                                    if (tuple.fldType.IsSubclassOf(typeof(UnityEngine.Object)))
                                        break;
                                    var list = ((Il2CppObjectBase)MainGenTools.CommonGet(obj, fieldName)!)
                                        .Cast<IList>();
                                    var rawEle = list![i];
                                    if (rawEle == null)
                                        continue;
                                    // 用「按真实类名重建的代理」去递归（否则 gen 表是空的），
                                    // 写回用原对象：两者指向同一个原生对象，写入原地生效。
                                    JsonCommonWarpper(Diag.Retype(rawEle), keyData[i]);
                                    list[i] = rawEle;
                                    StatSet++;
                                }
                                else if (tuple.fldType.IsArray)
                                {
                                    // var ele_type = field.FieldType.GetElementType();
                                    if (tuple.fldType.IsSubclassOf(typeof(UnityEngine.Object)))
                                        break;
                                    var array = ((Il2CppObjectBase)MainGenTools.CommonGet(obj, fieldName)!)
                                        .Cast<Array>();
                                    object? ele = null;
                                    try
                                    {
                                        ele = array.GetValue(i);
                                    }
                                    catch (Exception e)
                                    {
                                        var id = "NullId";
                                        if (obj is UniqueIDScriptable uniqueIDScriptable)
                                        {
                                            id = uniqueIDScriptable.UniqueID;
                                        }
                                        else if (obj is ScriptableObject scriptableObject)
                                        {
                                            id = scriptableObject.name;
                                        }

                                        Debug.LogWarning($"On access {id}::{objType}.{fieldName} : {e}");
                                    }

                                    if (ele == null)
                                        continue;
                                    // 递归用「真实类名代理」，写回用原对象（同一原生对象）
                                    JsonCommonWarpper(Diag.Retype(ele), keyData[i]);
                                    array.SetValue((Object)ele, i);
                                    // [FIX] 同上：SetValue 已原地生效，写回 Il2CppSystem.Array 到具体数组字段类型会抛异常
                                    StatSet++;
                                }
                                else if (Diag.IsIl2CppArrayTypePublic(tuple.fldType))
                                {
                                    // ★ [2026-10-02 关键补漏] JSON 里"数组元素是嵌套对象"这一类字段：
                                    //   DismantleActions / ProducedCards / DroppedCards 全是 Il2CppReferenceArray<T>，
                                    //   而 interop 里 Il2CppReferenceArray 是**类**，`fldType.IsArray` 为 false
                                    //   → 上面那两条分支都不命中 → 5 层引用链（卡→动作→集合→掉落→卡）在第 1 层就断了。
                                    //   这里补上：元素是 mod 私有副本（DeepDetach 建的），原地递归 warp 只影响自己。
                                    if (tuple.fldType.IsSubclassOf(typeof(UnityEngine.Object)))
                                        break;
                                    var arrObj = MainGenTools.CommonGet(obj, fieldName);
                                    if (arrObj == null)
                                        continue;
                                    var arrLen = (int)Diag.ElemCount(arrObj);
                                    if (MiniLoader.DiagFull && Diag.ArrWarpTrace < 25)
                                    {
                                        Diag.ArrWarpTrace++;
                                        MelonLogger.Msg("[WARPARR] " + objType.Name + "." + fieldName
                                                        + " 运行时长度=" + arrLen + " json元素=" + keyData.Count);
                                    }

                                    for (var k = 0; k < keyData.Count && k < arrLen; k++)
                                    {
                                        if (!keyData[k].IsObject)
                                            continue;
                                        var ele2 = Diag.Retype(Diag.GetElem(arrObj, k));
                                        if (ele2 == null)
                                            continue;
                                        JsonCommonWarpper(ele2, keyData[k]);
                                        Diag.SetElem(arrObj, k, ele2);
                                        StatSet++;
                                    }

                                    StatSet++;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception inner)
            {
                StatEx++;
                Diag.NoteWarpLoopExit(objType.Name, "catch(异常被吞)", key, __i, __total);
                // 无上限：异常采样全量记录 + 直接告警（零静默）
                StatExSample.Add(objType.Name + "." + key + " → " + inner.GetType().Name + ": " + inner.Message);
                Diag.NoteInlineIssue(objType.Name + "." + key + "|warp 异常",
                    inner.GetType().Name + " " + inner.Message);
            }
        }   // ← 关闭 foreach (var key in __keys)
            // ★ [WARP-LOOP] 无条件"循环走完"判据（不再依赖"最后一个键恰好是 WarpType 键"）
            Diag.NoteWarpLoopDone(objType.Name, __i, __total, __last);   // ★ 每类首次一行 + 汇总（不再逐实例打 ✗）
            if (false) MelonLogger.Msg("[WARP-LOOP] 循环结束: 宿主=" + objType.Name + " 处理=" + __i + "/总=" + __total
                            + " 最后到达的键=" + __last);
    }

    // ═══════════ 纯托管值类型副本（boxed struct）：按**托管成员**逐键 warp ═══════════
    /// <summary>本轮经"托管成员"通道写进结构副本的键数（判据用；成功只计数，不逐条打）。</summary>
    public static int ManagedWrites;
    /// <summary>其中经 **public 字段**写进去的键数（blittable 值类型那条路，正是本轮修好的）。</summary>
    public static int ManagedFieldWrites;
    /// <summary>其中经 **属性**写进去的键数。</summary>
    public static int ManagedPropWrites;

    /// <summary>
    /// `DurabilitiesConditions` / `DurabilityWeightValue` / `EncounterVariable` / `EnemySkillModifier` /
    /// `LightSourceSettings` / `Vector2` / `Color` 这类 **blittable 值类型**在设备上的 interop 程序集里是
    /// `ExplicitLayout` 的 **C# struct + public 字段**（没有属性）。主 warp 只处理
    /// `*WarpData` / 对象 / 数组，**标量一律跳过** —— 而这些结构的 JSON 键大量是标量（`x`/`y`/`r`/`g`/`b`/`Active`…）
    /// ⇒ 旧逻辑静默跳过 ⇒ 字段永远保持默认值。
    /// 这里按**托管成员**（属性优先、public 字段兜底）逐键写：对象键递归、标量键精确转换。
    /// 只用托管反射，**零字段偏移、零裸内存**。
    /// </summary>
    public static void ManagedStructWarp(object obj, KVProvider json, int depth = 0)
    {
        if (obj == null || json == null || !json.IsObject) return;
        if (depth > 8)   // 环保护（值类型本身不成环，但成员可能是类实例）
        {
            Diag.NoteInlineIssue(obj.GetType().Name + "|托管结构递归过深", "depth=" + depth + "（已停止下潜）");
            return;
        }

        var t = obj.GetType();
        var __total = 0; foreach (var __k in json.Keys) __total++;   // [WARP-LOOP] 总键数（只读）
        var __i = 0; var __last = "-";
        foreach (var key in json.Keys)
        {
            __i++; __last = key;
            StatKeys++;
            try
            {
                if (key.EndsWith("WarpData")) continue;   // 引用 warp 键由主路径处理，纯托管结构不涉及
                var kd = json[key];
                if (!Diag.TryReadMember(obj, key, out var cur, out var mt, out var canWrite, out var viaField))
                {
                    StatSkipped++;
                    Diag.NoteInlineIssue(t.Name + "." + key + "|托管结构无此成员",
                        "interop 结构里没有该名字的属性/字段 → 该键未写入");
                    continue;
                }

                if (viaField) ManagedFieldWrites++;
                else ManagedPropWrites++;

                if (!canWrite)
                {
                    StatSkipped++;
                    Diag.NoteInlineIssue(t.Name + "." + key + "|托管成员只读", "该结构成员没有 setter");
                    continue;
                }

                if (kd.IsObject)
                {
                    if (cur == null)
                    {
                        cur = mt is { IsValueType: true } ? Activator.CreateInstance(mt) : Diag.NewElementOf(mt);
                        if (cur == null)
                        {
                            StatSkipped++;
                            Diag.NoteInlineIssue(t.Name + "." + key + "|子结构实例创建失败",
                                "类型=" + (mt?.Name ?? "?"));
                            continue;
                        }
                    }

                    ManagedStructWarp(cur, kd, depth + 1);          // 递归填子结构
                    if (Diag.TryWriteMember(obj, key, cur))
                    {
                        StatSet++;
                        ManagedWrites++;
                    }
                    else
                    {
                        StatSkipped++;
                        Diag.NoteInlineIssue(t.Name + "." + key + "|子结构写回被拒", "类型=" + (mt?.Name ?? "?"));
                    }
                }
                else if (Diag.TryConvertScalar(mt, kd, out var val))
                {
                    if (Diag.TryWriteMember(obj, key, val))
                    {
                        StatSet++;
                        ManagedWrites++;
                    }
                    else
                    {
                        StatSkipped++;
                        Diag.NoteInlineIssue(t.Name + "." + key + "|标量写回被拒",
                            "目标类型=" + (mt?.Name ?? "?") + " 值=" + kd);
                    }
                }
                else
                {
                    StatSkipped++;
                    Diag.NoteInlineIssue(t.Name + "." + key + "|托管结构键未处理",
                        (kd.IsArray ? "数组键" : "标量类型不支持") + " 目标类型=" + (mt?.Name ?? "?"));
                }
            }
            catch (Exception e)
            {
                StatEx++;
                Diag.NoteInlineIssue(t.Name + "." + key + "|托管结构 warp 异常", e.GetType().Name + " " + e.Message);
            }
        }
    }
}
