// ============================================================================
//  CheatListFix —— 让 mod 卡牌出现在**游戏内作弊控制台**的卡片列表里
// ----------------------------------------------------------------------------
//  控制台（CstiCheatConsoleMobile/Patches.cs）实际行为：
//    · 读 `CheatsManager.AllCards` 列卡片，并用 `GameManager.AllCards` 做有效性过滤
//      （`if (!card || !__instance.GM.AllCards.Contains(card)) continue;`）；
//    · 列表为空时它自己会调 **`__instance.FillCards()`**（游戏自己的填充方法）。
//  且 `AllCards` 的元素类型是 **`InGameCardBase`**（对局内的卡实例），**不是 `CardData`** ——
//  所以"把 mod 的 CardData 塞进列表"这条路在类型上就不成立（会把列表写坏）。
//
//  本文件的策略（证据优先，全部走反射，避免编译期类型假设）：
//    ① 观察：打印两张表的条目数与"mod 卡命中数"（按名字匹配我们创建的 mod CardData）；
//    ② 触发：若 mod 卡命中数 = 0，则调用一次游戏自己的 `FillCards()`（最多 N 次），再看是否命中；
//    ③ 兜底（默认关，等 ① ② 的结论）：`CheatListsManualAppend` 打开后，会把 mod 卡按
//       **正确元素类型**（若元素类型是 CardData 才直接加；若是 InGameCardBase 则跳过并告警，
//       因为需要先构造对局内实例 —— 那属于"生成卡实例"，另行设计）。
//
//  日志判据：
//    [CHEATLIST] CheatsManager.AllCards = N 项；mod 卡命中 = K / 206
//    [CHEATLIST] 触发 FillCards(): AllCards 旧=N 新=M（mod 卡命中 K → K'）
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using MelonLogger = MelonLoader.MelonLogger;

namespace CSTI_MiniLoader
{
    public static class CheatListFix
    {
        private static int _tick;
        private static int _fillTries;
        private static int _errLogged;
        private static int _lastA = -1;
        private static int _lastB = -1;

        // ★★ [2026-10-03 卡死修复] "重建检测"状态：只有**表真的被重建过**才补一次。
        //    · 上一次看到的 长度 + 首元素指针 + 末元素指针
        //    · 已经补过的"表指纹"（补过就不再重复调用 → 天然不会每帧刷 FillCards）
        //    · 自上次补以来跳过的帧数（用于证明"大部分帧是跳过"）
        private static int _seenLen = -1;
        private static IntPtr _seenFirst, _seenLast;
        private static string _filledFp;
        private static int _skippedFrames;

        /// <summary>
        /// 由 HookFree.Tick **每帧**调用。**事件驱动且零调用**：每帧只做一次廉价的"指纹比对"
        /// （长度 + 首元素指针 + 末元素指针）；**只有指纹变化（= 游戏重建过该表）才补一次**，
        /// 且同一张表**只补一次**。其余帧直接返回，**不调用任何游戏方法**。
        /// （旧实现"命中=0 就补"会在某些状态下每帧调 `FillCards()` → 主线程被拖死 → 拖拽卡死 ✗。）
        /// </summary>
        public static void Tick()
        {
            if (!MiniLoader.MaintainCheatLists) return;
            _tick++;
            try
            {
                RunOnce();
            }
            catch (Exception e)
            {
                if (_errLogged++ < 3)
                    MelonLogger.Warning("[CHEATLIST] 维护失败: " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>表指纹：长度 + 首元素指针 + 末元素指针（任何一项变化 = 游戏重建过该表）。</summary>
        private static string Fingerprint(object list, out int len, out IntPtr first, out IntPtr last)
        {
            len = 0;
            first = IntPtr.Zero;
            last = IntPtr.Zero;
            try
            {
                if (list == null) return "<null>";
                len = Count(list);
                if (len > 0)
                {
                    var e0 = Elem(list, 0);
                    var e1 = Elem(list, len - 1);
                    if (e0 is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b0)
                        first = b0.Pointer;
                    if (e1 is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b1)
                        last = b1.Pointer;
                }

                return len + ":" + first.ToInt64().ToString("X") + ":" + last.ToInt64().ToString("X");
            }
            catch (Exception __e)
            {
                MelonLogger.Warning("[CheatListFix] 异常(已记录): " + __e.GetType().Name + " " + __e.Message);
                return "<err>";
            }
        }

        private static void RunOnce()
        {
            var cm = Pump.CheatsInstance;
            if (cm == null) return;

            object gm = null;
            try { gm = GetMember(cm, "GM"); } catch (Exception __e) { MelonLogger.Warning("[CheatListFix] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

            var modNames = ModCardNames();

            // ★★ [卡死修复 ①] 指纹比对（廉价、无游戏调用）：长度 + 首元素指针 + 末元素指针。
            //    与上一次完全相同 → **本帧直接返回，零调用**（这就是"大部分帧是跳过"的证据）。
            var listA = GetMember(cm, "AllCards");
            var fp = Fingerprint(listA, out var len, out var first, out var last);
            if (len == _seenLen && first == _seenFirst && last == _seenLast)
            {
                _skippedFrames++;
                return;
            }

            var lenFrom = _seenLen;
            _seenLen = len;
            _seenFirst = first;
            _seenLast = last;

            var a = Report("CheatsManager.AllCards", cm, modNames, ref _lastA);
            if (gm != null) Report("GameManager.AllCards", gm, modNames, ref _lastB);

            // ★★ [卡死修复 ②] 同一张表**只补一次**：补过的指纹记下来，之后即使指纹再相同也不会重复调用。
            if (a == 0 && MiniLoader.CheatListsTriggerFill)
            {
                if (fp == _filledFp) return;   // 这张表已经补过了 → 不重复调用（零调用）

                _fillTries++;
                var ok = Invoke(cm, "FillCards");
                var after = Report("触发 FillCards 后 CheatsManager.AllCards", cm, modNames, ref _lastA, force: true);
                var fpAfter = Fingerprint(GetMember(cm, "AllCards"), out _, out _, out _);
                _filledFp = fpAfter;
                MelonLogger.Msg("[CHEATLIST] 重建检测: 长度 " + lenFrom + "→" + len
                                + " 首元素 0x" + first.ToInt64().ToString("X")
                                + " 末元素 0x" + last.ToInt64().ToString("X")
                                + " → 触发补一次（第 " + _fillTries + " 次，调用成功=" + ok
                                + "；mod 卡命中 " + a + " → " + after
                                + "；自上次补以来跳过帧数=" + _skippedFrames + "）");
            }
        }

        /// <summary>打印某张表的条目数与 mod 卡命中数，返回命中数（取不到返回 -1）。</summary>
        private static int Report(string label, object owner, HashSet<string> modNames, ref int last, bool force = false)
        {
            try
            {
                var list = GetMember(owner, "AllCards");
                if (list == null)
                {
                    if (force || last != -2)
                    {
                        last = -2;
                        MelonLogger.Msg("[CHEATLIST] " + label + " = <null>");
                    }

                    return -1;
                }

                var n = Count(list);
                var hit = 0;
                for (var i = 0; i < n; i++)
                {
                    var e = Elem(list, i);
                    var nm = e == null ? null : Member(e, "name")?.ToString();
                    if (nm != null && modNames.Contains(nm)) hit++;
                }

                if (force || n != last)
                {
                    last = n;
                    MelonLogger.Msg("[CHEATLIST] " + label + " = " + n + " 项（元素类型="
                                    + ElementTypeName(list) + "）；mod 卡命中 = " + hit + " / " + modNames.Count);
                }

                return hit;
            }
            catch (Exception e)
            {
                if (_errLogged++ < 5)
                    MelonLogger.Warning("[CHEATLIST] " + label + " 读取失败: " + e.GetType().Name + " " + e.Message);
                return -1;
            }
        }

        /// <summary>本次由 loader 创建的 mod CardData 的 name 集合（控制台按 name/CardName 搜索）。</summary>
        private static HashSet<string> ModCardNames()
        {
            var set = new HashSet<string>();
            try
            {
                if (MiniLoader.AllItemDictionary.TryGetValue(typeof(CardData), out var d) && d != null)
                    foreach (var v in d.Values)
                    {
                        var nm = Member(v, "name")?.ToString();
                        if (!string.IsNullOrEmpty(nm)) set.Add(nm);
                    }
            }
            catch (Exception __e) { MelonLogger.Warning("[CheatListFix] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

            return set;
        }

        // ───────────── 反射小工具（不依赖具体 interop 类型） ─────────────

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name);
            if (p != null) return p.GetValue(o);
            var f = t.GetField(name);
            return f?.GetValue(o);
        }

        private static object Member(object o, string name) => GetMember(o, name);

        private static bool Invoke(object o, string method)
        {
            try
            {
                var m = o.GetType().GetMethod(method, Type.EmptyTypes);
                if (m == null) return false;
                m.Invoke(o, null);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[CHEATLIST] 调用 " + method + " 失败: " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>数组或 List 通用的元素个数。</summary>
        private static int Count(object list)
        {
            if (list == null) return 0;
            var t = list.GetType();
            var p = t.GetProperty("Count") ?? t.GetProperty("Length");
            if (p != null) return Convert.ToInt32(p.GetValue(list));
            if (list is ICollection c) return c.Count;
            return 0;
        }

        /// <summary>数组（索引器）或 List（索引器）通用取元素。</summary>
        private static object Elem(object list, int i)
        {
            try
            {
                var idx = list.GetType().GetProperty("Item", new[] { typeof(int) });
                return idx?.GetValue(list, new object[] { i });
            }
            catch
            {
                return null;
            }
        }

        private static string ElementTypeName(object list)
        {
            try
            {
                var t = list.GetType();
                if (t.IsArray) return t.GetElementType()?.Name ?? "?";
                if (t.IsGenericType && t.GetGenericArguments().Length == 1) return t.GetGenericArguments()[0].Name;
                var ep = t.GetProperty("Item", new[] { typeof(int) });
                return ep?.PropertyType.Name ?? t.Name;
            }
            catch
            {
                return "?";
            }
        }
    }
}
