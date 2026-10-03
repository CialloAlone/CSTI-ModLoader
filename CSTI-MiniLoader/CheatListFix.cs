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
        private static int _lastFillMs = -100000;
        private static int _errLogged;
        private static int _lastA = -1;
        private static int _lastB = -1;

        /// <summary>
        /// 由 HookFree.Tick **每帧**调用。[掩盖审计 P4] 事件驱动：不再"每 4 秒轮询 + 20 秒冷却"，
        /// 而是每帧比对 `AllCards` 的长度/首元素 —— 游戏一重建该表，下一次 Tick 立刻补回。
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

        private static void RunOnce()
        {
            var cm = Pump.CheatsInstance;
            if (cm == null) return;

            object gm = null;
            try { gm = GetMember(cm, "GM"); } catch (Exception __e) { MelonLogger.Warning("[CheatListFix] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

            var modNames = ModCardNames();

            var a = Report("CheatsManager.AllCards", cm, modNames, ref _lastA);
            if (gm != null) Report("GameManager.AllCards", gm, modNames, ref _lastB);

            // ② mod 卡一张都没命中 → 让游戏自己重填一次（它内部会走游戏的卡库）
            //    [掩盖审计 P4] 事件驱动：命中=0 就补，**没有固定冷却** —— 游戏在进档/开界面重建该表后，
            //    下一次 Tick（每帧）就会把 mod 卡补回去；不再用"等 20 秒"来掩盖"不知道它何时重建"。
            if (a == 0 && MiniLoader.CheatListsTriggerFill)
            {
                _fillTries++;
                var ok = Invoke(cm, "FillCards");
                var after = Report("触发 FillCards 后 CheatsManager.AllCards", cm, modNames, ref _lastA, force: true);
                MelonLogger.Msg("[CHEATLIST] 命中=0 → 立刻补（第 " + _fillTries + " 次）: 调用成功=" + ok
                                + "；mod 卡命中 " + a + " → " + after);
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
