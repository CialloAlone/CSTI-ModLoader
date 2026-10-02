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
    public static readonly System.Collections.Generic.List<string> SkipKeySamples = new();
    /// <summary>内联值类型直写的日志条数上限（避免刷屏）。</summary>
    public static int InlineLogged;

    /// <summary>
    /// "只解引用"模式：warp 只处理 `*WarpType`（即 `*WarpData` 的引用解析），跳过普通容器/标量。
    /// 专供**新建元素**：那些字段已由 `Diag.DeserializeElement`（ICall）连原引用一起填好，
    /// 再走普通 warp 会按 ADD 重建数组 → 原元素引用丢失（真机 7 处）。
    /// </summary>
    public static bool WarpKeysOnly;

    /// <summary>同上，但保证调用结束后恢复原值（可嵌套安全）。</summary>
    public static void JsonWarpKeysOnly(object obj, KVProvider json)
    {
        var saved = WarpKeysOnly;
        WarpKeysOnly = true;
        try
        {
            JsonCommonWarpper(obj, json);
        }
        finally
        {
            WarpKeysOnly = saved;
        }
    }
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
               + " List原地改已跳过=" + StatListSkipped + " 嵌套对象原地改已跳过=" + StatObjSkipped;
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

        // ★★ [2026-10-03 GSM 关键修复] 目标对象常常是**游戏自带对象**，Il2CppInterop 把注册表里的
        //    游戏对象统一包成基类 `UniqueIDScriptable` → `obj.GetType()` 是基类 →
        //    `MainGen.GetOrGen(基类)` 的表里没有 `DismantleActions`/`CardInteractions`
        //    → 每个键都走 `genInfos.TryGetValue(...) == false → continue`（**静默跳过**）
        //    → 63 条 GSM「目标解析成功但一个字都没写进去」（真机实测：成功=0 无变化=63）。
        //    按 il2cpp 真实类名重建代理后，字段表才是 CardData 的完整（含继承）表。
        var retyped = Diag.Retype(obj);
        if (retyped != null) obj = retyped;

        var objType = obj.GetType();
        var genInfos = MainGen.GetOrGen(objType);
        if (genInfos.Count == 0)
        {
            StatEmptyType++;
            if (StatSkipSample.Count < 5)
                StatSkipSample.Add("gen 表为空（该类型没有任何 NativeFieldInfoPtr 字段）: " + objType.FullName);
        }

        foreach (var key in json.Keys)
        {
            StatKeys++;
            try
            {
                // ★ [2026-10-03 第 5 轮] "只解引用"模式：新建元素已经用 `DeserializeElement`（ICall 反序列化）
                //    把**普通容器/标量**（`CardDropChanceModifiers`/`TimeOfDayMods`/`NOTAffectedThings`…）
                //    连同其**原始引用**一起填好了；若紧接着再走一遍普通 warp，ADD 逻辑会**重建这些数组** →
                //    原元素引用丢失（真机实测 7 处）。所以这一步只处理 `*WarpType`（即 `*WarpData` 的解引用），
                //    其它键一律不碰。开关只在"新建元素"路径上打开，正常 mod 对象不受影响。
                if (WarpKeysOnly && !key.EndsWith("WarpType")) continue;

                var keyData = json[key];
                if (key.EndsWith("WarpType"))
                {
                    if (!keyData.IsInt || !json.ContainsKey(key.Substring(0, key.Length - 8) + "WarpData"))
                        continue;
                    var fieldName = key.Substring(0, key.Length - 8);
                    if (!genInfos.TryGetValue(fieldName, out var tuple))
                    {
                        StatSkipped++;
                        if (SkipKeySamples.Count < 30)
                            SkipKeySamples.Add(objType.Name + "." + fieldName + "（gen表无此字段）");
                        if (StatSkipSample.Count < 5) StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
                        continue;
                    }
                    var fieldWarpData = json[fieldName + "WarpData"];
                    MainGenTools.CommonSet((Il2CppObjectBase)obj, fieldName, fieldWarpData, (WarpType)keyData.Int);
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
                            if (SkipKeySamples.Count < 30)
                                SkipKeySamples.Add(objType.Name + "." + fieldName + "（gen表无此字段）");
                            if (StatSkipSample.Count < 5) StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
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
                            if (MiniLoader.GsmInlineWrite &&
                                Diag.TrySetManagedField(obj, fieldName, keyData))
                            {
                                StatSet++;
                            }
                            else
                            {
                                StatSkipped++;
                                if (SkipKeySamples.Count < 30)
                                    SkipKeySamples.Add(objType.Name + "." + fieldName
                                                       + "（内联值类型：安全通道未启用或属性 setter 不可用 → 跳过）");
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
                                if (NestedCreated < 12)
                                {
                                    NestedCreated++;
                                    MelonLogger.Msg("[INL] 嵌套对象已新建并挂上: " + objType.Name + "." + fieldName
                                                    + " 类型=" + tuple.fldType.Name);
                                }

                                subObj = createdSub;
                            }
                            else
                            {
                                StatSkipped++;
                                if (SkipKeySamples.Count < 30)
                                    SkipKeySamples.Add(objType.Name + "." + fieldName + "（嵌套对象为 null 且新建失败）");
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
                            if (StatSkipSample.Count < 5) StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
                            continue;
                        }

                        for (var i = 0; i < keyData.Count; i++)
                        {
                            if (keyData[i].IsObject)
                            {
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
                if (StatExSample.Count < 5)
                    StatExSample.Add(objType.Name + "." + key + " → " + inner.GetType().Name + ": " + inner.Message);
            }
        }
    }
}