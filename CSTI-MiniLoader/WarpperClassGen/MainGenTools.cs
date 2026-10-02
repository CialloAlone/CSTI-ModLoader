using System;
using System.Linq;
using CSTI_MiniLoader.LoadUtil;
using HarmonyLib;
using MelonLoader;
using Il2CppSystem.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;
using UnityEngine;
using Array = Il2CppSystem.Array;
using Object = Il2CppSystem.Object;

namespace CSTI_MiniLoader.WarpperClassGen;

public static class MainGenTools
{
    /// <summary>诊断：只跟踪前 N 次 CommonSet，用来在原生崩溃前定位最后处理的字段。</summary>
    public static int SetTrace, ArrTrace, LiTrace;

    /// <summary>崩溃前最后一行也要保住：直接落盘（不走日志缓冲）。</summary>
    public static void Trace(string s)
    {
        if (TraceBudget-- <= 0) return;
        Diag.TraceLine(s);
    }
    public static int TraceBudget = 60000;

    public static object? CommonGet(object baseObj, string fld)
    {
        if (baseObj is not Il2CppObjectBase)
        {
            return Traverse.Create(baseObj).Field(fld).GetValue();
        }

        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return null;
        if (tuple.isValueType)
        {
            // [FIX] 内联值类型字段（IL2CPP struct）不能按指针解引用读取，直接跳过（原实现用 IntPtr 读 8 字节，值无意义）
            return null;
        }

        return AccessTools.Method(typeof(MainGenTools), nameof(CommonGetCls), null, [tuple.fldType])
            .Invoke(null, [baseObj, fld]);
    }

    public static T CommonGetVal<T>(Il2CppObjectBase baseObj, string fld)
        where T : struct
    {
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return default;
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        unsafe
        {
            return *(T*)(objHandle + tuple.fOffset);
        }
    }

    public static T? CommonGetCls<T>(Il2CppObjectBase baseObj, string fld)
        where T : Il2CppObjectBase
    {
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return null;
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        unsafe
        {
            var intPtr = *(IntPtr*)(objHandle + tuple.fOffset);
            return intPtr == IntPtr.Zero
                ? null
                : (T)AccessTools.DeclaredConstructor(typeof(T), [typeof(IntPtr)]).Invoke([intPtr]);
        }
    }

    public static void CommonSetFld(object baseObj, string fld, object? data)
    {
        if (baseObj is not Il2CppObjectBase)
        {
            Traverse.Create(baseObj).Field(fld).SetValue(data);
            return;
        }

        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        if (tuple.isValueType)
        {
            // [FIX] 内联值类型字段：不做写回（原实现把裸 IntPtr 写回去，等于破坏该 struct）
            return;
        }

        AccessTools.DeclaredMethod(typeof(MainGenTools), nameof(CommonSetFldCls), null, [tuple.fldType])
            .Invoke(null, [baseObj, fld, data]);
    }

    public static void CommonSetFldVal<T>(Il2CppObjectBase baseObj, string fld, T data)
        where T : struct
    {
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        unsafe
        {
            *(T*)(objHandle + tuple.fOffset) = data;
        }
    }

    public static void CommonSetFldCls<T>(Il2CppObjectBase baseObj, string fld, T data)
        where T : Il2CppObjectBase
    {
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset,
            IL2CPP.Il2CppObjectBaseToPtr(data));
    }

    /// <summary>
    /// 把 WarpData 里的字符串 ID 解析成对象。
    /// 原实现只查 `AllItemDictionary[typeof(T)]`，而游戏自带的 2858 个对象在本机是按 GUID 注册进
    /// UniqueIDScriptable.AllUniqueObjects 的（Il2CppInterop 把它们的包装类型统一成 UniqueIDScriptable，
    /// 所以「按具体类型分」的字典里根本没有它们）→ 引用全部解析失败。这里补一条回查游戏注册表的路。
    /// </summary>
    public static bool TryResolveRef<T>(string id, out T item) where T : Il2CppObjectBase
    {
        item = null;
        if (string.IsNullOrEmpty(id)) return false;
        var trace = Diag.ResolveTrace < 40;
        if (trace) Diag.ResolveTrace++;
        try
        {
            if (AllItemDictionary.TryGetValue(typeof(T), out var typed) &&
                typed.TryGetValue(id, out var o) && o is T t0)
            {
                item = t0;
                if (trace) MelonLogger.Msg("[RESOLVE] " + typeof(T).Name + " id=" + id + " → mod字典命中 " + Diag.NameOf(t0));
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var reg = UniqueIDScriptable.AllUniqueObjects;
            if (reg != null && reg.TryGetValue(id, out var uid) && uid != null)
            {
                var t1 = Diag.CastOrNull<T>(uid);
                if (t1 != null)
                {
                    item = t1;
                    if (trace) MelonLogger.Msg("[RESOLVE] " + typeof(T).Name + " id=" + id + " → 游戏注册表命中 " + Diag.NameOf(t1));
                    return true;
                }
            }
        }
        catch
        {
        }

        if (trace) MelonLogger.Msg("[RESOLVE] " + typeof(T).Name + " id=" + id + " → 未解析 ✗");
        return false;
    }

    /// <summary>
    /// C：GUID 两条路都失败后，按「类型 + 名字」再试一次（原版 CardTag / EquipmentTag / AudioClip / Sprite
    /// 在 mod JSON 里是按名字引用的，且它们不派生自 UniqueIDScriptable → 拿不到 GUID 键）。
    /// </summary>
    public static bool TryResolveRefByName<T>(string name, out T item) where T : Il2CppObjectBase
    {
        item = null;
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            var o = Diag.NameIndexFind(typeof(T).Name, name);
            if (o == null && typeof(T).BaseType != null)
                o = Diag.NameIndexFind(typeof(T).BaseType.Name, name);   // 子类名对不上时退到基类名
            if (o == null)
            {
                Diag.CountNameHit(false);
                return false;
            }

            item = Diag.CastOrNull<T>(o);
            if (item != null)
            {
                Diag.CountNameHit(true);
                if (MiniLoader.DiagFull)   // lean 下只累计计数（原先一次冷启动几千条逐条日志）
                    MelonLogger.Msg("[NAMEIDX] ✓ 按名解析 " + typeof(T).Name + " \"" + name + "\"");
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static void CommonSet(Il2CppObjectBase baseObj, string fld, KVProvider warpData, WarpType warpType)
    {
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        var ft = tuple.fldType;

        // 只有泛型类型才能取 GenericTypeDefinition（原来直接调用，非泛型字段会抛 InvalidOperationException）
        bool isIl2CppArray = false, isList = false;
        try
        {
            if (ft.IsGenericType)
            {
                var gtd = ft.GetGenericTypeDefinition();
                var bt = gtd.BaseType;
                // [FIX] 泛型基类比较必须经 GetGenericTypeDefinition()：
                // typeof(D<>).BaseType 得到的是 B<T!0>，直接和 typeof(B<>) 比较永远为 false
                isIl2CppArray = bt != null && bt.IsGenericType &&
                                bt.GetGenericTypeDefinition() == typeof(Il2CppArrayBase<>);
                isList = gtd == typeof(List<>);
            }
        }
        catch
        {
        }

        if (MiniLoader.DiagFull && SetTrace < 40)
        {
            SetTrace++;
            MelonLogger.Msg("[SET] " + baseObj.GetType().Name + "." + fld + " ft=" + (ft?.Name ?? "?")
                            + " 类别=" + (isIl2CppArray ? "数组" : isList ? "List" : warpData.IsString ? "字符串" : "其它")
                            + " cnt=" + warpData.Count + " warpType=" + warpType);
        }

        Trace("[SET] " + baseObj.GetType().Name + "." + fld + " ft=" + (ft?.FullName ?? "?")
              + " arr=" + isIl2CppArray + " list=" + isList + " cnt=" + warpData.Count + " wt=" + warpType);

        if (warpData is { IsArray: true } && isIl2CppArray)
        {
            if (warpData.Count == 0 || warpData[0].IsString)
            {
                var methodInfo =
                    AccessTools.Method(typeof(MainGenTools), nameof(SetArrByWarpper), null,
                        [ft.GetGenericArguments().First()]);
                methodInfo.Invoke(null, [baseObj, fld, warpData, warpType]);
            }
            else if (warpData.Count > 0 && warpData[0].IsObject)
            {
                var methodInfo =
                    AccessTools.Method(typeof(MainGenTools), nameof(SetArrNoWarpper), null,
                        [ft.GetGenericArguments().First()]);
                methodInfo.Invoke(null, [baseObj, fld, warpData, warpType]);
            }
        }
        else if (warpData is { IsArray: true } && isList)
        {
            if (warpData.Count == 0 || warpData[0].IsString)
            {
                var methodInfo =
                    AccessTools.Method(typeof(MainGenTools), nameof(SetLiByWarpper), null,
                        [ft.GetGenericArguments().First()]);
                methodInfo.Invoke(null, [baseObj, fld, warpData, warpType]);
            }
            else if (warpData.Count > 0 && warpData[0].IsObject)
            {
                var methodInfo =
                    AccessTools.Method(typeof(MainGenTools), nameof(SetLiNoWarpper), null,
                        [ft.GetGenericArguments().First()]);
                methodInfo.Invoke(null, [baseObj, fld, warpData, warpType]);
            }
        }
        else if (warpData.IsString)
        {
            var methodInfo =
                AccessTools.Method(typeof(MainGenTools), nameof(SetByWarpper), null, [ft]);
            methodInfo.Invoke(null, [baseObj, fld, warpData, warpType]);
        }
    }

    public static void SetByWarpper<T>(Il2CppObjectBase baseObj, string fld, KVProvider warpData, WarpType warpType)
        where T : Il2CppObjectBase
    {
        if (TryResolveRef<T>(warpData.ToString(), out var item) ||
            TryResolveRefByName<T>(warpData.ToString(), out item))   // C：GUID 失败后按「类型+名字」再试
        {
            var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
            // [FIX] 字段偏移必须按「字段宿主」的类型查，不能用元素类型 T 查（原来用 typeof(T) 永远查不到）
            var valueTuples = MainGen.GetOrGen(baseObj.GetType());
            if (!valueTuples.TryGetValue(fld, out var tuple)) return;
            if (tuple.isValueType)
            {
                unsafe
                {
                    *(T*)(objHandle + tuple.fOffset) = item;
                }
            }
            else
            {
                IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset,
                    IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)(object)item));
            }
        }
    }

    public static void SetLiByWarpper<T>(Il2CppObjectBase baseObj, string fld, KVProvider warpData, WarpType warpType)
        where T : Il2CppObjectBase
    {
        {
            var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
            // [FIX] 同上：字段宿主是 baseObj，不是元素类型 T
            var valueTuples = MainGen.GetOrGen(baseObj.GetType());
            if (!valueTuples.TryGetValue(fld, out var tuple)) return;
            unsafe
            {
                var li = *(IntPtr*)(objHandle + tuple.fOffset);
                var list = li != IntPtr.Zero ? new List<T>(li) : new List<T>();
                if (warpType == WarpType.MODIFY) list.Clear();
                for (var i = 0; i < warpData.Count; i++)
                {
                    if (TryResolveRef<T>(warpData[i].ToString(), out var item) ||
                        TryResolveRefByName<T>(warpData[i].ToString(), out item))
                    {
                        list.Add(item);
                    }
                }

                if (LiTrace < 25)
                {
                    LiTrace++;
                    MelonLogger.Msg("[LI] " + baseObj.GetType().Name + "." + fld + " 原有=" + (li == IntPtr.Zero ? "null" : "有")
                                    + " 请求=" + warpData.Count + " 结果=" + list.Count);
                }

                // [FIX] 原来算完 list 就丢了，从没写回字段
                IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset,
                    IL2CPP.Il2CppObjectBaseToPtr(list));
                if (LiTrace < 25) MelonLogger.Msg("[LI] 写回完成 " + fld);
            }
        }
    }

    public static void SetLiNoWarpper<T>(Il2CppObjectBase baseObj, string fld, KVProvider warpData, WarpType warpType)
    {
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        unsafe
        {
            var li = *(IntPtr*)(objHandle + tuple.fOffset);
            var list = li != IntPtr.Zero ? new List<T>(li) : new List<T>();
            if (warpType == WarpType.MODIFY) list.Clear();
            for (var i = 0; i < warpData.Count; i++)
            {
                var scriptableObject = typeof(T).IsSubclassOf(typeof(ScriptableObject))
                    ? (T)(object)ScriptableObject.CreateInstance(Il2CppType.Of<T>())
                    : AccessTools.CreateInstance<T>();
                WarpFunc.JsonCommonWarpper(scriptableObject, warpData[i]);
                list.Add(scriptableObject);
            }

            // [FIX] 写回
            IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset,
                IL2CPP.Il2CppObjectBaseToPtr(list));
        }
    }

    public static void SetArrByWarpper<T>(Il2CppObjectBase baseObj, string fld, KVProvider warpData, WarpType warpType)
        where T : Il2CppObjectBase
    {
        {
            Trace("[ARR] 进入 " + baseObj.GetType().Name + "." + fld + " 请求=" + warpData.Count);
            var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
            var valueTuples = MainGen.GetOrGen(baseObj.GetType());
            if (!valueTuples.TryGetValue(fld, out var tuple)) return;
            unsafe
            {
                var arr = *(IntPtr*)(objHandle + tuple.fOffset);
                Trace("[ARR] 旧数组=0x" + arr.ToInt64().ToString("X"));
                var cacheTLi = warpType == WarpType.MODIFY || arr == IntPtr.Zero
                    ? new System.Collections.Generic.List<T>()
                    : new Il2CppReferenceArray<T>(arr).ToList();
                Trace("[ARR] 旧数组解析 OK 项数=" + cacheTLi.Count);
                for (var i = 0; i < warpData.Count; i++)
                {
                    if (TryResolveRef<T>(warpData[i].ToString(), out var item) ||
                        TryResolveRefByName<T>(warpData[i].ToString(), out item))
                    {
                        cacheTLi.Add(item);
                    }
                }

                Trace("[ARR] 解析完成 结果=" + cacheTLi.Count + " 偏移=" + tuple.fOffset);

                if (ArrTrace < 25)
                {
                    ArrTrace++;
                    MelonLogger.Msg("[ARR] " + baseObj.GetType().Name + "." + fld + " arr原有=" + (arr == IntPtr.Zero ? "null" : "有")
                                    + " 请求=" + warpData.Count + " 解析到=" + cacheTLi.Count);
                }

                // [FIX] 不用托管索引器 / Il2CppClassPointerStore 分配（真机实测在填充时 SIGSEGV）：
                // 用字段自身的 il2cpp 类型 il2cpp_array_new，再按数组头偏移直接写槽位。
                var fieldKlass = IL2CPP.il2cpp_class_from_il2cpp_type(IL2CPP.il2cpp_field_get_type(tuple.fPtr));
                Trace("[ARR] 字段数组类=0x" + fieldKlass.ToInt64().ToString("X") + " 元素数=" + cacheTLi.Count);
                if (fieldKlass == IntPtr.Zero) return;

                var arrPtr = IL2CPP.il2cpp_array_new(fieldKlass, (ulong)cacheTLi.Count);
                Trace("[ARR] 新数组=0x" + arrPtr.ToInt64().ToString("X"));
                if (arrPtr == IntPtr.Zero) return;

                var header = IntPtr.Size == 8 ? 0x20 : 0x10;
                for (var i = 0; i < cacheTLi.Count; i++)
                {
                    var slot = arrPtr + header + i * IntPtr.Size;
                    IL2CPP.il2cpp_gc_wbarrier_set_field(arrPtr, slot, IL2CPP.Il2CppObjectBaseToPtr(cacheTLi[i]));
                }

                Trace("[ARR] 填充完成");
                IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset, arrPtr);
                Trace("[ARR] 写回完成 " + fld);
                if (ArrTrace < 25) MelonLogger.Msg("[ARR] 写回完成 " + fld);
            }
        }
    }

    public static void SetArrNoWarpper<T>(Il2CppObjectBase baseObj, string fld, KVProvider warpData, WarpType warpType)
        where T : Il2CppObjectBase
    {
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        unsafe
        {
            var arr = *(IntPtr*)(objHandle + tuple.fOffset);
            var cacheTLi = warpType == WarpType.MODIFY || arr == IntPtr.Zero
                ? new System.Collections.Generic.List<T>()
                : new Il2CppReferenceArray<T>(arr).ToList();
            for (var i = 0; i < warpData.Count; i++)
            {
                var scriptableObject = typeof(T).IsSubclassOf(typeof(ScriptableObject))
                    ? (T)(object)ScriptableObject.CreateInstance(Il2CppType.Of<T>())
                    : AccessTools.CreateInstance<T>();
                WarpFunc.JsonCommonWarpper(scriptableObject, warpData[i]);
                cacheTLi.Add(scriptableObject);
            }

            var newArr = Array.CreateInstance(Il2CppType.Of<T>(), cacheTLi.Count);
            for (var i = 0; i < cacheTLi.Count; i++)
            {
                newArr.SetValue((Object)(Il2CppObjectBase)cacheTLi[i], i);
            }

            IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset,
                IL2CPP.Il2CppObjectBaseToPtr(newArr));
        }
    }
}