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
                var keyData = json[key];
                if (key.EndsWith("WarpType"))
                {
                    if (!keyData.IsInt || !json.ContainsKey(key.Substring(0, key.Length - 8) + "WarpData"))
                        continue;
                    var fieldName = key.Substring(0, key.Length - 8);
                    if (!genInfos.TryGetValue(fieldName, out var tuple))
                    {
                        StatSkipped++;
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
                            if (StatSkipSample.Count < 5) StatSkipSample.Add("字段未生成: " + objType.Name + "." + fieldName);
                            continue;
                        }
                        if (tuple.fldType.IsSubclassOf(typeof(UnityEngine.Object)))
                            continue;
                        // [FIX] 内联值类型子对象：读/写都会破坏内存（真机 SIGSEGV），直接跳过
                        if (tuple.isValueType)
                        {
                            StatSkipped++;
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
                            StatSkipped++;
                            continue;
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