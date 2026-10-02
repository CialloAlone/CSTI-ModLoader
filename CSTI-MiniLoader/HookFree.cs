using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace CSTI_MiniLoader
{
    /// <summary>
    /// 免 hook 注入模块。
    ///
    /// 背景（真机实测）：
    ///   MiniLoader 原来用三个 Harmony 补丁把 mod 内容注册进游戏：
    ///     ① LocalizationManager.LoadLanguage(后缀)  ② GuideManager.Start(前缀)  ③ GraphicsManager.Init(后缀)
    ///   这三个方法一挂 Harmony，游戏就会在启动/读档阶段崩溃（补丁体是否执行无关）。
    ///   而支持模块的 OnUpdate 泵也是坏的（SM_Component.Create() 被我们打断）。
    ///
    /// 现方案：**完全不 hook 这三个方法**，改由 Pump（挂在已验证安全的 CheatsManager.Update 上的每帧泵）驱动：
    ///   ① 只依赖静态成员 LocalizationManager.CurrentTexts / Instance → 轮询注入
    ///   ②③ 用 FindObjectsOfType 找到组件实例后，直接调用原来的 Add* 逻辑
    /// </summary>
    public static class HookFree
    {
        private static bool _probed;
        private static bool _locDone;
        private static bool _resDone;
        private static bool _guideDone;
        private static bool _gfxDone;
        private static bool _gfxOnceDone;
        private static int _guideTries;
        private static int _gfxTries;
        private static bool _errLogged;
        private static bool _gfxChainLogged;
        private static int _gfxWaitLog;
        private static int _ticks;
        private static bool _shimPrimed;

        /// <summary>反射调用 CstiICallFix.RealShims.PrimeTemplates()（避免硬依赖）。</summary>
        private static void PrimeShimTemplates()
        {
            if (_shimPrimed) return;
            _shimPrimed = true;
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = a.GetType("CstiICallFix.RealShims"); } catch { }
                    if (t != null) break;
                }
                if (t == null) { MelonLogger.Msg("[HOOKFREE] 未找到 CstiICallFix（跳过模板表预建）"); return; }
                t.GetMethod("PrimeTemplates", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            }
            catch (Exception e) { MelonLogger.Warning("[HOOKFREE] PrimeTemplates 调用失败: " + e.Message); }
        }

        public static bool FindApisAvailable { get; private set; }

        public static void Tick()
        {
            try
            {
                if (!_probed) { _probed = true; ProbeFindApis(); }
                _ticks++;

                // ---------- ⓪ 游戏资源注册（原版 FindObjectsOfType 路径在本机必失败）----------
                if (!_resDone)
                {
                    if (LoadUtil.LoadResources.LoadGameResourceFromRegistry())
                    {
                        _resDone = true;
                        SelfCheck.CaptureBefore();                                   // 记基线（游戏注册表条目数）
                        PrimeShimTemplates();                                        // ① 先让 ICallFix 建好克隆模板表（暴露 shim 的前提）
                        if (MiniLoader.DeferredInit) MiniLoader.RunDeferredInit();   // ② 再读包 + Warpper（此时 ScriptableObject 创建可走 shim）
                        SelfCheck.Dump();                                            // 自检：mod 内容是否真的注册进游戏
                    }
                }

                // ---------- ① 本地化（纯静态，已实测生效）----------
                if (!_locDone && LocalizationManager.CurrentTexts != null)
                {
                    try
                    {
                        Patchers.LoadPatchMain.LoadLocalizationPublic();
                        _locDone = true;
                        MelonLogger.Msg("[HOOKFREE] ① 本地化已注入（轮询，未使用 Harmony）");
                        try { Diag.DumpLocalizationWindyKeys(); } catch { }
                    }
                    catch (Exception e) { LogErr("① 本地化注入", e); _locDone = false; }
                }

                // ---------- ② GuideManager（拿到实例后注入一次）----------
                if (!_guideDone && FindApisAvailable && (MiniLoader.InitDone || !MiniLoader.DeferredInit))
                {
                    var g = FindFirst<GuideManager>();
                    if (g != null)
                    {
                        try
                        {
                            Patchers.LoadPatchMain.LoadGuideEntryPublic(g);
                            Patchers.LoadPatchMain.AddPlayerCharacterPublic(g);
                            _guideDone = true;
                            MelonLogger.Msg("[HOOKFREE] ② GuideManager 注入完成（轮询，未使用 Harmony）");
                        }
                        catch (Exception e)
                        {
                            if (++_guideTries <= 3) LogErr("② GuideManager 注入", e);
                        }
                    }
                }

                // ---------- ③ GraphicsManager（拿到实例后注入）----------
                if (!_gfxDone && (MiniLoader.InitDone || !MiniLoader.DeferredInit))
                {
                    GraphicsManager gm = Pump.GraphicsInstance;   // 首选：由 GraphicsManager.Update 捕获
                    try
                    {
                        if (gm == null)
                        {
                            var cm = Pump.CheatsInstance as CheatsManager;
                            var game = (cm != null) ? cm.GM : null;
                            gm = (game != null) ? game.GameGraphics : null;
                            if (++_gfxWaitLog % 600 == 1)
                                MelonLogger.Msg("[HOOKFREE] ③ 等待实例: Cheats=" + (cm != null) + " GM=" + (game != null)
                                                + " GameGraphics=" + (game != null && game.GameGraphics != null));
                        }
                    }
                    catch (Exception e)
                    {
                        if (!_gfxChainLogged) { _gfxChainLogged = true; MelonLogger.Warning("[HOOKFREE] ③ 引用链取实例失败: " + e.Message); }
                    }
                    if (gm == null && FindApisAvailable) gm = FindFirst<GraphicsManager>();
                    if (gm != null)
                    {
                        try
                        {
                            Patchers.LoadPatchMain.AddCardTabGroupPublic(gm);
                            Patchers.LoadPatchMain.AddBlueprintCardDataPublic(gm);
                            Patchers.LoadPatchMain.AddVisibleGameStatPublic(gm);
                            if (!_gfxOnceDone)
                            {
                                Patchers.LoadPatchMain.AddCardTabGroupOncePublic(gm);
                                Patchers.LoadPatchMain.CustomGameObjectFixedPublic();
                                Patchers.LoadPatchMain.AddCardFilterGroupOncePublic();
                                _gfxOnceDone = true;
                            }
                            _gfxDone = true;
                            MelonLogger.Msg("[HOOKFREE] ③ GraphicsManager 注入完成（轮询，未使用 Harmony）");
                        }
                        catch (Exception e)
                        {
                            if (++_gfxTries <= 3) LogErr("③ GraphicsManager 注入", e);
                        }
                    }
                }
            }
            catch (Exception e) { LogErr("Tick", e); }

            // ---------- ④ 观察：特质页签的 ContainedPerks 有没有被游戏重写（供用户验收时判断时机）----------
            if (MiniLoader.InitDone) { try { Diag.CheckTabCountsTick(); } catch { } }

            // ---------- ⑤ 维护作弊控制台的两张卡表（mod 卡可见性；幂等、每约 4 秒一次）----------
            if (MiniLoader.InitDone) { try { CheatListFix.Tick(); } catch { } }
        }

        private static void LogErr(string what, Exception e)
        {
            if (_errLogged) return;
            _errLogged = true;
            MelonLogger.Warning("[HOOKFREE] " + what + " 失败: " + e.GetType().Name + " " + e.Message);
        }

        /// <summary>用已注册的 FindObjectsOfType ICall 找第一个组件实例（不 hook 任何东西）。</summary>
        private static T FindFirst<T>() where T : UnityEngine.Object
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>());
                if (arr != null && arr.Length > 0)
                {
                    var o = arr[0];
                    return o == null ? null : o.TryCast<T>();
                }
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; MelonLogger.Warning("[HOOKFREE] FindObjectsOfType(" + typeof(T).Name + ") 失败: " + e.Message); }
            }
            return null;
        }

        private static void ProbeFindApis()
        {
            var names = new[]
            {
                "UnityEngine.Object::FindObjectOfType",
                "UnityEngine.Object::FindObjectsOfType",
                "UnityEngine.Object::FindObjectFromInstanceID",
            };
            var ok = false;
            foreach (var n in names)
            {
                var p = RawTexture.ResolveIcall(n);
                if (p != IntPtr.Zero) ok = true;
            }
            FindApisAvailable = ok;
            MelonLogger.Msg("[HOOKFREE] 实例查找 API 可用 = " + ok);
        }
    }
}
