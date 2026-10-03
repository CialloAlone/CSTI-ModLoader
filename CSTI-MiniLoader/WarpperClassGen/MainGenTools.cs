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
            // #1 [掩盖审计] 不再返回 null：**读是安全的** → 用生成的托管属性读。
            try
            {
                var prop = baseObj.GetType().GetProperty(fld);
                if (prop != null && prop.CanRead) return prop.GetValue(baseObj);
                Diag.NoteInlineIssue(baseObj.GetType().Name + "." + fld + "|读值类型无可读属性", "该字段读不到（不是 null 语义）");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[GEN] 读值类型字段失败: " + baseObj.GetType().Name + "." + fld + " : " + e.GetType().Name + " " + e.Message);
            }

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
            // #2/#3 [掩盖审计] 值类型字段的**通用写入通道**（零裸内存、零静默）：
            //   ① data 是 JSON → 代理 → warp → 属性 setter 整块写回；
            //   ② data 是**运行时对象**（模板克隆/深拷贝/嵌套对象挂载都会这样传）→
            //      **源对象属性 getter 读 → 目标对象属性 setter 写**（同名字段按值拷贝）。
            //      真机实测：这条路径缺失导致 14,000 条"[GEN] 值类型字段无法写回（数据不是 JSON）"
            //      —— `LocalizedString`（ActionDescription/CustomDestroyMessage/LogText/VictoryMessage…）
            //      这一整类内联结构字段**从来没被拷过去**，是"动作字段看着有、实际空"的另一半原因。
            if (data is KVProvider kvp)
            {
                if (!Diag.TrySetStructViaProxy(baseObj, fld, kvp))
                    Diag.NoteInlineIssue(baseObj.GetType().Name + "." + fld + "|JSON 写回失败",
                        "无可写属性 setter，该结构字段未被赋值");
            }
            else if (data != null)
            {
                // ★★ [2026-10-03 第 10 轮 · 修"源→目标"分支的**语义错位**]
                //   这条分支有两种调用形态：
                //     ① **值的直接写回**（`DeepDetach`/`DetachPlainClassChildren` 的去共享新实例；
                //        以及"嵌套值类型子对象 warp 完写回"）—— data 就是"要写进目标字段的那个对象"；
                //     ② **同类型整对象拷贝**（模板克隆）—— 需要"源同名成员 get → 目标同名成员 set"。
                //   旧代码只实现了 ②，而且用 `src.GetType().GetProperty(fld)` 去**源对象上找目标字段名**
                //   （例如 `LocalizedString.CardName`）→ 必然为 null ⇒ 真机 68 类 / 4 万+ 次失败，
                //   且**数据根本没写回**（去共享的子对象仍与原对象共享 = 潜在的跨卡污染）。
                //   现在：先按类型匹配**直接写回**（①），类型不匹配再回退 ②。TryWriteMember 会做
                //   可赋值性检查，不匹配时**什么都不写**，所以两条语义不会互相污染。
                var ok = false;
                if (Diag.TryWriteMember(baseObj, fld, data))
                {
                    Diag.MemberDirectWrites++;
                    ok = true;
                }
                else if (data is Il2CppObjectBase src)
                {
                    try
                    {
                        var pt = baseObj.GetType().GetProperty(fld);
                        var ps = src.GetType().GetProperty(fld);
                        if (pt != null && pt.CanWrite && ps != null && ps.CanRead)
                        {
                            pt.SetValue(baseObj, ps.GetValue(src));
                            Diag.StructProxyWrites++;
                            ok = true;
                        }
                    }
                    catch (Exception __e)
                    {
                        Diag.NoteInlineIssue(baseObj.GetType().Name + "." + fld + "|源→目标拷贝异常",
                            __e.GetType().Name + " " + __e.Message);
                        ok = true;   // 已上报，不再重复计数
                    }

                    if (!ok)
                        Diag.NoteInlineIssue(baseObj.GetType().Name + "." + fld + "|无可用成员(源→目标)",
                            "源类型=" + src.GetType().Name + " 目标类型=" + baseObj.GetType().Name);
                }
                else
                {
                    Diag.NoteInlineIssue(baseObj.GetType().Name + "." + fld + "|数据类型不支持",
                        "data=" + data.GetType().Name);
                }
            }
            else
            {
                Diag.NoteInlineIssue(baseObj.GetType().Name + "." + fld + "|数据类型不支持", "data=null");
            }

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
        catch (Exception __e) { MelonLogger.Warning("[MainGenTools] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

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
        catch (Exception __e) { MelonLogger.Warning("[MainGenTools] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

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
            // [形态分派审计] 不再退到基类名（同属兜底掩盖）
            // ★ [形态分派审计] 删除"全桶按名找"（NameIndexFindAny）：它把"类型不匹配"掩盖成"碰巧找到"。
            //   现在只查该字段声明类型自己的名字索引；查不到就是查不到，并把索引规模打出来。
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
        catch (Exception __e) { MelonLogger.Warning("[MainGenTools] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

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
        catch (Exception __e) { MelonLogger.Warning("[MainGenTools] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

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
        if (Diag.ResolveByJsonForm<T>(warpData.ToString(), fld, out var item))   // ★ 按 JSON 形态分派（禁止互相兜底）
        {
            var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
            // [FIX] 字段偏移必须按「字段宿主」的类型查，不能用元素类型 T 查（原来用 typeof(T) 永远查不到）
            var valueTuples = MainGen.GetOrGen(baseObj.GetType());
            if (!valueTuples.TryGetValue(fld, out var tuple)) return;
            if (tuple.isValueType)
            {
                // #3 [2026-10-03 掩盖审计] **删除裸指针写** `*(T*)(base+offset)=item`：
                // 对含引用的内联结构写 8 字节指针会破坏内存，而且它掩盖了"结构字段没有安全写路径"。
                // 统一走通解：代理 → warp → 属性 setter 整块写回；失败**打日志**（零静默）。
                if (!Diag.TrySetStructViaProxy(baseObj, fld, warpData))
                    MelonLogger.Warning("[GEN] SetByWarpper 值类型字段写回失败（无可用属性 setter）: "
                                        + baseObj.GetType().Name + "." + fld);
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

                // ★ 同数组守卫：只解引用阶段不许重建**非空** List（保住反序列化带进来的原引用）

                var list = li != IntPtr.Zero ? new List<T>(li) : new List<T>();
                // [2026-10-03] 不再整表清空：MODIFY 也改成"就地改同下标、余额追加"，避免把原元素换掉。
                var liOriginal = list.Count;
                for (var i = 0; i < warpData.Count; i++)
                {
                    if (Diag.ResolveByJsonForm<T>(warpData[i].ToString(), fld, out var item))   // ★ 按 JSON 形态分派
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

            // ★ 同数组守卫：只解引用阶段不许重建**非空** List（保住反序列化带进来的原引用）

            var list = li != IntPtr.Zero ? new List<T>(li) : new List<T>();

            // ★ [2026-10-03 第 4 轮] 不再 `list.Clear()`（MODIFY 清空会丢原引用 = 真机那 2 个"原元素被替换"）。
            //    改成与数组路径一致：MODIFY → 同下标**就地写原元素**；ADD/ADD_REFERENCE → 尾部追加新元素。
            //    新元素同样先走**无条件 ICall 反序列化**（填值类型字段），再 warp 解析引用。
            var originalCount = list.Count;
            var inPlace = 0;
            var appended = 0;
            for (var i = 0; i < warpData.Count; i++)
            {
                var el = warpData[i];

                if (warpType == WarpType.MODIFY && i < list.Count && list[i] != null)
                {
                    if (el != null && el.IsObject)
                    {
                        Diag.DeserializeElement((Il2CppObjectBase)(object)list[i], el, "List就地改");
                        WarpFunc.JsonCommonWarpper(list[i], el);
                        inPlace++;
                        continue;
                    }

                    RefReplaceRefused++;      // 字符串形式：只给了引用、没有字段可写 → 保留原元素
                    continue;
                }

                if (el != null && el.IsObject)
                {
                    var createdObj = Diag.NewElementOf(typeof(T));
                    var created = createdObj == null ? default : (T)createdObj;
                    if (created == null)
                    {
                        created = typeof(T).IsSubclassOf(typeof(ScriptableObject))
                            ? (T)(object)ScriptableObject.CreateInstance(Il2CppType.Of<T>())
                            : AccessTools.CreateInstance<T>();
                    }

                    if (created == null) continue;

                    Diag.CurrentPhase = "新建元素追加(List)";
                    Diag.DeserializeElement((Il2CppObjectBase)(object)created, el, "List追加");
                    WarpFunc.JsonCommonWarpper(created, el);   // P1：精确语义，无需守卫
                    Diag.DumpTriggerFields(created, el, baseObj.GetType().Name + "." + fld + "[" + i + "]（List追加）");
                    list.Add(created);
                    appended++;
                }
            }

            if (inPlace > 0 || appended > 0)   // 只打"确实动过"的；无数量上限（零 cap、零静默）
            {
                LiNoWarpperLogged++;
                MelonLogger.Msg("[ARR] 对象元素列表(NoWarpper): " + baseObj.GetType().Name + "." + fld
                                + " 原有=" + originalCount + " → " + list.Count
                                + "（就地改=" + inPlace + " 追加=" + appended + " 模式=" + warpType + "）");
            }

            // [FIX] 写回
            IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + tuple.fOffset,
                IL2CPP.Il2CppObjectBaseToPtr(list));
        }
    }

    /// <summary>本轮经 warp「按对象新建元素并追加」的条数（GSM 判据用）。</summary>
    public static int CreatedByWarp;
    private static int WarpObjSkipped;

    /// <summary>引用保留核对（真损坏判据）：原有元素总数 / 其中原生指针原样保留数 / 丢引用告警次数。</summary>
    public static int GsmOriginalTotal, GsmOriginalPreserved;

    /// <summary>新增元素里"用 Unity 反序列化填过全部字段"的个数 / 反序列化失败次数（空字段判据）。</summary>
    public static int WarpElementDeserialized, WarpElementDeserFail;
    private static int DeserDiagLogged;
    private static int GsmRefLossLogged;

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

                // ★ 同 SetArrNoWarpper 的守卫：只解引用阶段不许重建**非空**容器
                //   （反序列化已把那批元素连同原引用填好；重建 = 丢引用）。

                Trace("[ARR] 旧数组=0x" + arr.ToInt64().ToString("X"));

                // ★★ [2026-10-03 真损坏修复] 原来 MODIFY(5) 走 `new List<T>()`（**清空重建**），
                //    而 GSM 的 `*WarpData` 元素是**对象** → 于是整组原动作被换成一批新建实例：
                //    真机实测 `DismantleActions: 6 项[Ignore it | Use Spear | …] → 6 项[ptr 全变、ActionName=-、ProducedCards=0]`
                //    —— 条数还对得上但内容全空 = **游戏数据被破坏**。
                //    现在两种模式**都从原元素开始**（引用原样保留）：
                //      · ADD(4) / ADD_REFERENCE(6)：只在**尾部追加**；
                //      · MODIFY(5)：同下标就**就地改那个原元素**（保引用），JSON 多出来的项才新建追加，
                //        原数组多出来的尾部元素**保留**（不再截断）。
                var existingArr = arr == IntPtr.Zero ? null : new Il2CppReferenceArray<T>(arr);
                var cacheTLi = existingArr == null
                    ? new System.Collections.Generic.List<T>()
                    : existingArr.ToList();
                var originalCount = cacheTLi.Count;
                var inPlaceModified = 0;
                var appendedNew = 0;
                Trace("[ARR] 旧数组解析 OK 项数=" + cacheTLi.Count + " 模式=" + warpType);
                for (var i = 0; i < warpData.Count; i++)
                {
                    // ★ [2026-10-03 GSM] `*WarpData` 的元素**本身是一个对象**（不是 GUID 字符串）时：
                    // GameSourceModify 就是这种形态（例如 `CardInteractionsWarpData: [ {…一个完整 CardAction…} ]`）。
                    // 老代码只把它 ToString() 后当 GUID 解析 → 必然失败 → "解析到 0 项" → 一条都追加不上。
                    var el = warpData[i];
                    if (el != null && el.IsObject)
                    {
                        // MODIFY：同下标已有元素 → **就地改它**（保住原引用与原内容）
                        if (warpType == WarpType.MODIFY && i < cacheTLi.Count && cacheTLi[i] != null)
                        {
                            WarpFunc.JsonCommonWarpper(cacheTLi[i], el);
                            inPlaceModified++;
                            continue;
                        }

                        var created = Diag.NewElementOf(typeof(T));
                        if (created is T createdT)
                        {
                            // ★★ [2026-10-03 新增元素"空字段"修复] 新建实例的值类型字段（int/enum/内联 struct，
                            //    例如 `DaytimeCost` 与 `LocalizedString ActionName`）**走不了 warp**
                            //    （warp 明确跳过内联值类型以免破坏内存）→ 追加出来的元素"条数对、内容全空"
                            //    （真机实测 Fibers/PalmTreeNew/LargeTree：ActionName="-" DaytimeCost=0 ProducedCards=0）。
                            //    先用 Unity 自己的反序列化把**全部字段**（含值类型/嵌套对象）写进去，
                            //    再走 warp 解析 `*WarpData` 引用；顺序不能反 —— 反序列化会把引用占位清空。
                            if (warpType != WarpType.ADD_REFERENCE)
                            {
                                var elJson = el.ToJson();
                                try
                                {
                                    // Lead 指定的两条诊断：json 原文 + 实际类型名（托管类型 / il2cpp 类 / T）
                                    if (DeserDiagLogged < 3)
                                    {
                                        DeserDiagLogged++;
                                        var cptr = Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr(createdT);
                                        MelonLogger.Msg("[ARR] 新增元素反序列化: 托管类型=" + createdT.GetType().FullName
                                                        + " il2cpp类=" + Diag.Cls(Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(cptr))
                                                        + " 元素类型T=" + typeof(T).Name
                                                        + " json前200=" + (elJson.Length > 200 ? elJson.Substring(0, 200) : elJson));
                                    }

                                    // 首选：直接调已确认可用的 ICall（显式传真实类型）；失败再用托管版兜底
                                    if (!Diag.FromJsonViaIcall(elJson, createdT))
                                    {
                                        UnityEngine.JsonUtility.FromJsonOverwrite(elJson,
                                            createdT.Cast<Il2CppSystem.Object>());   // 指针级 cast
                                    }

                                    WarpElementDeserialized++;
                                }
                                catch (Exception de)
                                {
                                    WarpElementDeserFail++;
                                    if (WarpElementDeserFail <= 5)
                                        MelonLogger.Warning("[ARR] 新增元素反序列化**抛异常**: " + baseObj.GetType().Name
                                                        + "." + fld + " : " + de.GetType().Name + " " + de.Message
                                                        + " | json=" + (elJson.Length > 150 ? elJson.Substring(0, 150) : elJson));
                                }
                            }

                            WarpFunc.JsonCommonWarpper(createdT, el);   // P1：精确 ADD 语义已保证原元素不动，无需"只解引用"守卫
                    Diag.DumpTriggerFields(createdT, el, baseObj.GetType().Name + "." + fld + "[" + i + "]");
                            cacheTLi.Add(createdT);
                            CreatedByWarp++;
                            appendedNew++;
                            continue;
                        }

                        if (WarpObjSkipped < 20)
                        {
                            WarpObjSkipped++;
                            MelonLogger.Warning("[ARR] 无法为对象元素新建实例: " + baseObj.GetType().Name
                                            + "." + fld + " 元素类型=" + typeof(T).Name);
                        }

                        continue;
                    }

                    if (Diag.ResolveByJsonForm<T>(el.ToString(), fld, out var item))   // ★ 按 JSON 形态分派
                    {
                        // ★ [2026-10-03 第 4 轮] MODIFY 下**绝不替换原引用**（真机实测有 2 个原元素被换掉 ✗）：
                        // 字符串形式只给了"已存在对象的引用"，没有字段可写 → **保留原元素**并记日志。
                        if (warpType == WarpType.MODIFY && i < cacheTLi.Count)
                        {
                            RefReplaceRefused++;
                            if (RefReplaceLogged < 10)
                            {
                                RefReplaceLogged++;
                                MelonLogger.Msg("[ARR] MODIFY 保持原元素（不替换引用）: " + baseObj.GetType().Name
                                                + "." + fld + " 下标=" + i + " 请求=" + item);
                            }
                        }
                        else
                        {
                            cacheTLi.Add(item);          // ADD / ADD_REFERENCE：追加**已存在对象**的引用
                            appendedNew++;
                        }
                    }
                }

                // 引用保留核对：原有多少个下标、其中多少个的原生指针原样保留
                var preserved = 0;
                for (var i = 0; i < originalCount; i++)
                {
                    try
                    {
                        if (existingArr != null && cacheTLi[i] != null &&
                            IL2CPP.Il2CppObjectBaseToPtr(cacheTLi[i]) == IL2CPP.Il2CppObjectBaseToPtr(existingArr[i]))
                            preserved++;
                    }
                    catch (Exception __e) { MelonLogger.Warning("[MainGenTools] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                }

                if (originalCount > 0)
                {
                    GsmOriginalTotal += originalCount;
                    GsmOriginalPreserved += preserved;
                    if (preserved < originalCount && GsmRefLossLogged < 10)
                    {
                        GsmRefLossLogged++;
                        MelonLogger.Warning("[ARR] ⚠ 原元素引用丢失: " + baseObj.GetType().Name + "." + fld
                                            + " 原有=" + originalCount + " 保留=" + preserved
                                            + " 模式=" + warpType + " 就地改=" + inPlaceModified + " 追加=" + appendedNew);
                    }
                }

                Trace("[ARR] 解析完成 结果=" + cacheTLi.Count + " 偏移=" + tuple.fOffset
                      + " 就地改=" + inPlaceModified + " 追加=" + appendedNew + " 引用保留=" + preserved + "/" + originalCount);

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
        Diag.LastArrayCallSite = "SetArrNoWarpper<" + typeof(T).Name + "> ← " + Diag.Frames(3);
        var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull(baseObj);
        var valueTuples = MainGen.GetOrGen(baseObj.GetType());
        if (!valueTuples.TryGetValue(fld, out var tuple)) return;
        unsafe
        {
            var arr = *(IntPtr*)(objHandle + tuple.fOffset);

            // ★★ [2026-10-03 第 7 轮 · 最后一处] 守卫**下沉到函数内部**：
            //    生成包装器/反射调用（栈里是 `RuntimeMethodInfo.InternalInvoke` → 本函数）会**绕过**
            //    已填好的**非空容器**按 ADD 重建 → 原元素引用丢失（真机 7 处：CardDropChanceModifiers /
            //    绝不允许"非空容器被重建"**。

            // ★★ [2026-10-03] 这里才是 GSM「对象元素数组」真正走的路（CommonSet 对 `warpData[0].IsObject`
            //    分派到 SetArrNoWarpper，而不是 SetArrByWarpper）—— 之前两轮修错了地方。
            //    旧行为两处硬伤：① `MODIFY` 直接从空表开始 = **清空重建**（原动作被换掉 = 真损坏）；
            //    ② 新元素只走 warp = **值类型字段（ActionName/DaytimeCost）全空**（warp 明确跳过内联值类型）。
            //    现在：两种模式都从**原元素**开始（保引用）；MODIFY 同下标**就地改**；
            //    新元素先用**已确认可用的 ICall** `JsonUtility::FromJsonInternal` 把全部字段填上（含值类型），
            //    再走 warp 解析 `*WarpData` 引用。零裸内存写入。
            var existingArr = arr == IntPtr.Zero ? null : new Il2CppReferenceArray<T>(arr);
            var cacheTLi = existingArr == null
                ? new System.Collections.Generic.List<T>()
                : existingArr.ToList();
            var originalCount = cacheTLi.Count;
            var inPlace = 0;
            var appended = 0;

            for (var i = 0; i < warpData.Count; i++)
            {
                // MODIFY：同下标已有元素 → 就地改（保引用、保原内容）
                if (warpType == WarpType.MODIFY && i < cacheTLi.Count && cacheTLi[i] != null)
                {
                    var it = warpData[i];
                    if (it != null && it.IsObject)
                    {
                        Diag.DeserializeElement(cacheTLi[i], it, "就地改");
                        WarpFunc.JsonCommonWarpper(cacheTLi[i], it);
                        inPlace++;
                        continue;
                    }
                }

                var el = warpData[i];
                if (el != null && el.IsObject)
                {
                    // 新建实例：优先 il2cpp_object_new（与元素真实类型一致），退回 Unity/AccessTools 两条老路
                    var createdObj = Diag.NewElementOf(typeof(T));
                    var created = createdObj == null ? default : (T)createdObj;
                    if (created == null)
                    {
                        created = typeof(T).IsSubclassOf(typeof(ScriptableObject))
                            ? (T)(object)ScriptableObject.CreateInstance(Il2CppType.Of<T>())
                            : AccessTools.CreateInstance<T>();
                    }

                    if (created == null)
                    {
                        if (WarpObjSkipped < 20)
                        {
                            WarpObjSkipped++;
                            MelonLogger.Warning("[ARR] 无法为对象元素新建实例: " + baseObj.GetType().Name
                                                + "." + fld + " 元素类型=" + typeof(T).Name);
                        }

                        continue;
                    }

                    // ① 先用 ICall 反序列化把**全部字段**（含值类型/内联结构）写进去
                    Diag.CurrentPhase = "新建元素追加(Array)";
                    Diag.DeserializeElement(created, el, "追加");
                    // ② 再**只解引用**（`*WarpData`），不再碰已由反序列化填好的普通容器 → 保住原始引用
                    WarpFunc.JsonCommonWarpper(created, el);
                    Diag.DumpTriggerFields(created, el, baseObj.GetType().Name + "." + fld + "[" + i + "]（NoWarpper追加）");
                    cacheTLi.Add(created);
                    appended++;
                }
                else
                {
                    if (Diag.ResolveByJsonForm<T>(el.ToString(), fld, out var item))   // ★ 按 JSON 形态分派
                        cacheTLi.Add(item);
                }
            }

            // 引用保留核对（★★ 第 6 轮改为**裸读原数组槽位**：与写回用同一套「头偏移 + IntPtr.Size」规则。
            //  之前用 `existingArr[i]`（托管索引器）读原元素，异常被 catch 吞掉 → 计成 0，
            //  于是出现"原有=1 保留=0"这种**可能是假象**的告警 —— 真机那 7 处正是从这行打出来的。）
            var preserved = 0;
            var skippedRead = 0;
            var hdr = IntPtr.Size == 8 ? 0x20 : 0x10;
            for (var i = 0; i < originalCount; i++)
            {
                try
                {
                    var oldPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(arr + hdr + i * IntPtr.Size);
                    var newPtr = cacheTLi[i] == null
                        ? IntPtr.Zero
                        : IL2CPP.Il2CppObjectBaseToPtr(cacheTLi[i]);
                    if (oldPtr != IntPtr.Zero && oldPtr == newPtr) preserved++;
                    else if (oldPtr == IntPtr.Zero) skippedRead++;
                }
                catch
                {
                    skippedRead++;
                }
            }

            if (originalCount > 0)
            {
                GsmOriginalTotal += originalCount;
                GsmOriginalPreserved += preserved;
                if (preserved < originalCount && GsmRefLossLogged < 10)
                {
                    GsmRefLossLogged++;
                    MelonLogger.Warning("[ARR] ⚠ 原元素引用丢失(NoWarpper): " + baseObj.GetType().Name + "." + fld
                                        + " 原有=" + originalCount + " 保留=" + preserved
                                        + " 模式=" + warpType + " 就地改=" + inPlace + " 追加=" + appended
                                        + " 读取失败=" + skippedRead
                                        + " | 阶段=" + Diag.CurrentPhase + " 调用来源=" + Diag.LastArrayCallSite);
                }
            }

            if (inPlace > 0 || appended > 0)   // 只打"确实动过"的；无数量上限（零 cap、零静默）
            {
                NoWarpperLogged++;
                MelonLogger.Msg("[ARR] 对象元素数组(NoWarpper): " + baseObj.GetType().Name + "." + fld
                                + " 原有=" + originalCount + " → " + cacheTLi.Count
                                + "（就地改=" + inPlace + " 追加=" + appended
                                + " 引用保留=" + preserved + "/" + originalCount + " 模式=" + warpType + "）");
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

    /// <summary>NoWarpper 路径的日志上限。</summary>
    public static int NoWarpperLogged;
    /// <summary>MODIFY 时"拒绝替换原引用"的次数（0 = 原元素一个都没被换掉）。</summary>
    public static int RefReplaceRefused;
    public static int LiNoWarpperLogged;   // 保留字段：旧计数不再用于限制

    /// <summary>"只解引用"阶段被守卫拦下的"非空容器重建"次数（预期 >0；拦下 = 原引用保住）。</summary>
    private static int RefReplaceLogged;
}
