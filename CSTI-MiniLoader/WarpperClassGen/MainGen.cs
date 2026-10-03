using System;
using MelonLoader;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;

namespace CSTI_MiniLoader.WarpperClassGen;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum WarpType
{
    NONE,
    COPY,
    CUSTOM,
    REFERENCE,
    ADD,
    MODIFY,
    ADD_REFERENCE
}

public static class MainGen
{
    /// <summary>
    /// 字段名 → (真实字段类型, NativeFieldInfoPtr, 字段偏移, 是否值类型)。
    ///
    /// [FIX 2026-10-02 真机] 原实现存的是 `NativeFieldInfoPtr_*` 这个 **IntPtr 静态字段** 的 FieldInfo，
    /// 于是 `tuple.fld.FieldType` 恒等于 `System.IntPtr`，连锁导致：
    ///   · `isValueType` 恒为 true → 嵌套对象 warp 用 CommonGetVal&lt;IntPtr&gt; 拿到裸指针
    ///     （日志「空gen表类型: System.IntPtr」× 5303 就是这么来的），嵌套对象一个都没写进去；
    ///   · `GetGenericTypeDefinition()` 对 IntPtr 抛 InvalidOperationException
    ///     → 所有「引用数组 / List」型字段的 warp 全灭（内部异常 × 275）。
    /// 真实类型必须从代理类上同名**属性**取（0.5.7/0.6 的代理类都只有 NativeFieldInfoPtr_* 字段 + 属性）。
    /// </summary>
    public static readonly
        Dictionary<Type, Dictionary<string, (Type fldType, IntPtr fPtr, int fOffset, bool isValueType)>>
        WarpperTypes = new();

    static MainGen()
    {
    }

    public static Dictionary<string, (Type fldType, IntPtr fPtr, int fOffset, bool isValueType)> GetOrGen(Type type)
    {
        if (WarpperTypes.TryGetValue(type, out var warpperType)) return warpperType;
        var warpper = new Dictionary<string, (Type fldType, IntPtr fPtr, int fOffset, bool isValueType)>();
        WarpperTypes[type] = warpper;

        // ★ [2026-10-02 关键补漏] 必须**连基类的字段一起收**：
        //   interop 代理类把每个字段都声明在**定义它的那个类**上，而原实现只扫 `GetDeclaredFields(type)`，
        //   于是继承字段完全不在 gen 表里 → warp 到不了它们。
        //   真正的断链例子：`DismantleCardAction` 自己只有 6 个字段，`ActionName` / `ProducedCards` 在基类
        //   （CardAction）上 → 事件选项 5 层引用链（卡→动作→集合→掉落→卡）在"动作"这层就断了，
        //   最内层 `DroppedCard` 永远是 null（真机判据 [EFFECT2] DroppedCard=<null>）。
        for (var t = type; t != null && t != typeof(object) && t != typeof(Il2CppObjectBase); t = t.BaseType)
        {
            foreach (var field in AccessTools.GetDeclaredFields(t))
            {
                if (!field.IsStatic || !field.Name.StartsWith("NativeFieldInfoPtr")) continue;
                var name = field.Name.Substring("NativeFieldInfoPtr_".Length);
                if (warpper.ContainsKey(name)) continue;      // 派生类优先
                var fPtr = (IntPtr)field.GetValue(null);
                var realType = field.FieldType;
                if (realType == typeof(IntPtr))
                {
                    try
                    {
                        // 真实类型同名属性可能在基类上 → 沿继承链找
                        for (var pt = type; pt != null && pt != typeof(object); pt = pt.BaseType)
                        {
                            var prop = pt.GetProperty(name,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                BindingFlags.DeclaredOnly);
                            if (prop == null) continue;
                            realType = prop.PropertyType;
                            break;
                        }
                    }
                    catch (Exception __e) { MelonLogger.Warning("[MainGen] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                }

                warpper[name] = (realType, fPtr, (int)IL2CPP.il2cpp_field_get_offset(fPtr), IsIl2CppValueType(fPtr));
            }
        }

        return warpper;
    }

    /// <summary>
    /// 用 il2cpp 原生元数据判断字段是不是「内联值类型」。
    /// 不能用 interop 代理的 PropertyType.IsValueType：IL2CPP 的 struct 在代理程序集里是**类**，
    /// 于是内联 struct 字段会被当成对象指针去 *(IntPtr*)(obj+offset) 解引用 → 拿到垃圾指针 → SIGSEGV
    /// （2026-10-02 真机实测：CardAction.RequiredReceivingDurabilities(ft=DurabilityConditions) 之后必崩）。
    /// </summary>
    private static bool IsIl2CppValueType(IntPtr fieldPtr)
    {
        try
        {
            var typePtr = IL2CPP.il2cpp_field_get_type(fieldPtr);
            if (typePtr == IntPtr.Zero) return false;
            var klass = IL2CPP.il2cpp_class_from_il2cpp_type(typePtr);
            if (klass == IntPtr.Zero) return false;
            return IL2CPP.il2cpp_class_is_valuetype(klass);
        }
        catch
        {
            return false;
        }
    }
}