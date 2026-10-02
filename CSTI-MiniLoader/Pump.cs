using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// 替代 Update 泵。
    ///
    /// 背景：MelonLoader 的 Il2Cpp 支持模块用
    ///   Main.obj.AddComponent(Il2CppType.Of&lt;SM_Component&gt;())
    /// 挂载驱动组件；真机上这条返回 null（随后 SiblingFix() NRE），我们为了能加载 mod 把
    /// Create() 打成了 ret —— 副作用是**所有 mod 的 OnUpdate / MelonCoroutines 全都不跑**。
    ///
    /// 本类用另一种方式驱动：Harmony 挂一个**每帧都会被调用的游戏方法**（目标可配置），
    /// 在里面调用注册进来的 tick 回调。经验证据：CheatsManager 的 OnGUI/Update 被
    /// CstiCheatConsoleMobile 长期 patch 且稳定 → 每帧类方法可以安全 patch；
    /// 而出问题的那三个（LocalizationManager.LoadLanguage / GuideManager.Start / GraphicsManager.Init）
    /// 都是启动早期的一次性初始化方法。
    /// </summary>
    public static class Pump
    {
        /// <summary>注册一个每帧回调（幂等）。</summary>
        public static void Register(Action tick)
        {
            if (tick == null) return;
            lock (Sync)
            {
                if (!Ticks.Contains(tick)) Ticks.Add(tick);
            }
        }

        private static readonly List<Action> Ticks = new List<Action>();
        private static readonly object Sync = new object();
        private static bool _installed;
        private static int _frames;
        private static bool _logged;
        private static int _errors;

        /// <summary>由 Harmony 补丁调用（每帧）。</summary>
        internal static void TickAll()
        {
            _frames++;
            if (!_logged && _frames >= 1)
            {
                _logged = true;
                MelonLogger.Msg("[PUMP] 每帧泵已生效（frames=" + _frames + "，tick 数=" + Ticks.Count + "）");
            }

            Action[] snapshot;
            lock (Sync) snapshot = Ticks.ToArray();
            foreach (var t in snapshot)
            {
                try { t(); }
                catch (Exception e)
                {
                    if (_errors++ < 5) MelonLogger.Warning("[PUMP] tick 异常: " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        /// <summary>
        /// 安装泵：在指定类型/方法上挂一个空的 Postfix。
        /// 默认目标 CheatsManager.Update（已被证明可安全 patch）。
        /// </summary>
        public static bool Install(string typeName = "CheatsManager", string methodName = "Update")
        {
            if (_installed) return true;
            try
            {
                var asm = FindGameAssembly();
                var t = asm?.GetType("CheatsManager") ?? asm?.GetType(typeName);
                if (t == null) { MelonLogger.Warning("[PUMP] 找不到类型 " + typeName); return false; }

                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null) { MelonLogger.Warning("[PUMP] 找不到方法 " + typeName + "." + methodName); return false; }

                var harmony = new HarmonyLib.Harmony("csti.miniloader.pump");
                var postfix = new HarmonyMethod(typeof(Pump).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(m, postfix: postfix);

                // 第二个目标：GraphicsManager.Update —— 只捕获实例，不做逻辑
                var gt = asm?.GetType("GraphicsManager");
                var gm2 = gt?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (gm2 != null)
                {
                    var cap = new HarmonyMethod(typeof(Pump).GetMethod(nameof(GraphicsPostfix), BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(gm2, postfix: cap);
                    MelonLogger.Msg("[PUMP] 已挂实例捕获: GraphicsManager.Update");
                }
                else MelonLogger.Warning("[PUMP] 没找到 GraphicsManager.Update");
                _installed = true;
                MelonLogger.Msg("[PUMP] 已挂每帧泵: " + typeName + "." + methodName);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PUMP] 安装失败: " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>每帧被 patch 的方法的实例（CheatsManager），用来顺着游戏自己的引用链取到其它管理器。</summary>
        public static object CheatsInstance { get; private set; }

        /// <summary>GraphicsManager 的实例（由它自己的 Update 每帧捕获，模式与泵一致）。</summary>
        public static GraphicsManager GraphicsInstance { get; private set; }

        /// <summary>捕获 GraphicsManager 实例（只存引用，不做任何逻辑）。</summary>
        internal static void CaptureGraphics(GraphicsManager inst)
        {
            if (inst != null) GraphicsInstance = inst;
        }

        private static void Postfix(CheatsManager __instance)
        {
            CheatsInstance = __instance;
            TickAll();
        }

        private static void GraphicsPostfix(GraphicsManager __instance) => CaptureGraphics(__instance);

        private static Assembly FindGameAssembly()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetType("CheatsManager") != null) return a; } catch { }
            }
            return null;
        }
    }
}
