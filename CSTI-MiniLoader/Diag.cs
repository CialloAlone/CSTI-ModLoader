using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CSTI_MiniLoader.LoadUtil;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CSTI_MiniLoader;

/// <summary>
/// 真机诊断 + 注册表按类名索引工具。
///
/// 背景（2026-10-02 真机实测）：
///   Harness 的注册表导入走 `UniqueIDScriptable.AllUniqueObjects`，用 `obj.GetType()` 当字典类型键。
///   Il2CppInterop 的对象池只会把「曾被具体类型包装过」的对象还原成具体类型，游戏自身的 2858 个对象
///   全部被包装成了 `UniqueIDScriptable` → 它们全都落进 ItemDictionary(typeof(UniqueIDScriptable))，
///   于是 `ItemDictionary(typeof(PerkGroup))` 里只剩 mod 自己那 1 个 PerkGroup（还是 GUID 键），
///   `AddPerkGroup()` 按「组名」查表必然查不到 → 新特质永远不会挂进任何特质组 → 玩家看不到。
///
/// 本文件用 il2cpp 原生元数据（IL2CPP.il2cpp_object_get_class → class_get_name）拿到**真实类名**，
/// 不依赖 Il2CppInterop 的包装类型，也不依赖任何被裁剪的 ICall。
/// </summary>
public static class Diag
{
    /// <summary>
    /// 崩溃前最后一行也要保住：直接落盘到可读目录（/sdcard/MelonLoader/&lt;pkg&gt;/csti_trace.log）。
    /// GameRootDirectory 在真机上是 app 私有外部目录，adb shell 读不了（tombstone 也是 Permission denied），
    /// 而 Mods 目录的父目录是可读的。
    /// </summary>
    private static System.IO.StreamWriter _tw;
    private static bool _twTried;

    public static void TraceLine(string s)
    {
        // [诊断瘦身] 逐行写文件在 full 下才做（lean/off 时连字符串拼接都省掉，只累计次数）。
        if (!MiniLoader.DiagFull)
        {
            TraceWrites++;
            return;
        }

        try
        {
            if (!_twTried)
            {
                _twTried = true;
                var mods = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
                var parent = System.IO.Path.GetDirectoryName(mods?.TrimEnd('/') ?? "");
                var path = System.IO.Path.Combine(parent ?? "/sdcard/MelonLoader", "csti_trace.log");
                _tw = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
                _tw.WriteLine("# csti_trace 开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            _tw?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    public static string Cls(IntPtr ptr)
    {
        try
        {
            if (ptr == IntPtr.Zero) return "<nullptr>";
            var cls = IL2CPP.il2cpp_object_get_class(ptr);
            if (cls == IntPtr.Zero) return "<nullcls>";
            var n = IL2CPP.il2cpp_class_get_name(cls);
            if (n == IntPtr.Zero) return "<noname>";
            return Marshal.PtrToStringAnsi(n) ?? "<?>";
        }
        catch (Exception e)
        {
            return "<err:" + e.GetType().Name + ">";
        }
    }

    public static string Cls(object o)
    {
        if (o == null) return "<null>";
        try
        {
            var b = o as Il2CppObjectBase;
            if (b != null && b.Pointer != IntPtr.Zero) return Cls(b.Pointer);
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        try { return o.GetType().Name; } catch { return "<err>"; }
    }

    public static string NameOf(object o)
    {
        if (o == null) return "<null>";
        try
        {
            if (o is Object uo) return uo.name ?? "<null-name>";
        }
        catch (Exception e)
        {
            return "<name-err:" + e.GetType().Name + ">";
        }

        return "<not-unity-object>";
    }

    /// <summary>安全读取 UniqueIDScriptable.UniqueID。</summary>
    public static string SafeUniqueId(object o)
    {
        try
        {
            if (o is UniqueIDScriptable u) return u.UniqueID ?? "<null>";
        }
        catch (Exception e)
        {
            return "<guid-err:" + e.GetType().Name + ">";
        }

        return "<not-uid>";
    }

    /// <summary>用指针比对判断列表里有没有这个特质（只用索引器，不依赖泛型 Contains）。</summary>
    private static bool ListHas(Il2CppSystem.Collections.Generic.List<CharacterPerk> list, CharacterPerk p)
    {
        if (list == null || p == null) return false;
        var pp = p.Pointer;
        var n = list.Count;
        for (var i = 0; i < n; i++)
        {
            var e = list[i];
            if (e != null && e.Pointer == pp) return true;
        }

        return false;
    }

    /// <summary>缺哪个补哪个，返回补登记个数。</summary>
    public static int EnsureModPerksIn(Il2CppSystem.Collections.Generic.List<CharacterPerk> list)
    {
        if (list == null || ModPerks.Count == 0) return 0;
        var added = 0;
        foreach (var p in ModPerks)
        {
            try
            {
                if (ListHas(list, p)) continue;
                list.Add(p);
                added++;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[TABFIX] Add 失败: " + e.GetType().Name + " " + e.Message);
                break;
            }
        }

        return added;
    }

    /// <summary>
    /// 每 4 秒维护一次各 PerkTabGroup 的 ContainedPerks（共约 16 分钟）：
    /// 游戏若在打开特质界面时重写/清空这个列表，这里会自动补登记，用户不必重启。
    /// 只在数量变化或每 15 次采样时打一行 [TABCHK]，避免刷屏。
    /// </summary>
    private static int _tabChkCount;
    private static DateTime _tabChkNext = DateTime.MinValue;
    private static string _lastTabSig;

    public static void CheckTabCountsTick()
    {
        if (_tabChkCount >= 240) return;
        if (DateTime.Now < _tabChkNext) return;
        _tabChkNext = DateTime.Now.AddSeconds(4);
        _tabChkCount++;
        try
        {
            var tabs = RegistryByClass("PerkTabGroup", 8);
            if (tabs.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in tabs)
            {
                var tab = CastOrNull<PerkTabGroup>(kv.Value);
                if (tab == null) continue;
                var list = tab.ContainedPerks;
                var added = EnsureModPerksIn(list);
                if (added > 0)
                    MelonLogger.Warning("[TABFIX] " + NameOf(tab) + " 补登记 " + added + " 个 mod 特质（列表被游戏重写过）");
                sb.Append(NameOf(tab)).Append('=').Append(list?.Count ?? -1).Append("  ");
            }

            var sig = sb.ToString();
            EnsureModPerksInMainMenu();
            if (sig != _lastTabSig || _tabChkCount % 15 == 1)
            {
                _lastTabSig = sig;
                MelonLogger.Msg("[TABCHK] #" + _tabChkCount + " " + sig);
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[TABCHK] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>找第一个组件实例（FindObjectsOfType 这条 ICall 在本机是注册的）。</summary>
    public static T FindFirstComp<T>() where T : UnityEngine.Object
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
            MelonLogger.Warning("[MENU] FindObjectsOfType(" + typeof(T).Name + ") 失败: " + e.GetType().Name + " " + e.Message);
        }

        return null;
    }

    /// <summary>
    /// 特质界面的真正数据源（[SCAN] 扫出来的）：
    ///   MainMenu.AllCharacterPerks : List&lt;CharacterPerk&gt;
    ///   MainMenu.UnlockedPerks     : List&lt;CharacterPerk&gt;
    ///   MainMenu.AllPerkTabs       : List&lt;PerkTabGroup&gt;
    /// 除了 PerkTabGroup.ContainedPerks，这里也把 mod 特质补齐（每 4 秒自愈一次）。
    /// </summary>
    private static int _menuMissingLogged;

    public static void EnsureModPerksInMainMenu()
    {
        if (ModPerks.Count == 0) return;
        try
        {
            var menu = FindFirstComp<MainMenu>();
            if (menu == null)
            {
                if (_menuMissingLogged++ == 0) MelonLogger.Msg("[MENU] 暂未找到 MainMenu 实例（可能还没进主菜单界面）");
                return;
            }

            int a, b;
            try { a = EnsureModPerksIn(menu.AllCharacterPerks); }
            catch (Exception e) { a = -1; MelonLogger.Warning("[MENU] AllCharacterPerks 补登记失败: " + e.GetType().Name + " " + e.Message); }
            try { b = EnsureModPerksIn(menu.UnlockedPerks); }
            catch (Exception e) { b = -1; MelonLogger.Warning("[MENU] UnlockedPerks 补登记失败: " + e.GetType().Name + " " + e.Message); }

            if (a != 0 || b != 0)
            {
                int tabs = -1, allPerks = -1;
                try { tabs = menu.AllPerkTabs?.Count ?? -1; } catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                MelonLogger.Warning("[MENUFIX] MainMenu 补登记: AllCharacterPerks+" + a + " UnlockedPerks+" + b
                                    + "（AllPerkTabs=" + tabs + "; AllCharacterPerks="
                                    + (SafeCount(menu.AllCharacterPerks)) + "; UnlockedPerks="
                                    + (SafeCount(menu.UnlockedPerks)) + "）");
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[MENU] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    private static int SafeCount(Il2CppSystem.Collections.Generic.List<CharacterPerk> l)
    {
        try { return l?.Count ?? -1; } catch { return -2; }
    }

    /// <summary>
    /// 探测「缺失 ICall 是不是只是名字对不上」。
    /// 线索：libunity.so 里没有 Sprite/Texture 的导出符号，但保留着这些名字字符串：
    ///   UnityEngine.Sprite::CreateSpriteWithoutTextureScripting_Injected
    ///   UnityEngine.Sprite::CreateSprite_Injected
    ///   UnityEngine.Texture2D::LoadRawTextureDataImpl / LoadRawTextureDataImplArray
    ///   UnityEngine.AudioClip::Construct_Internal / CreateUserSound
    /// managed 侧解析的是不带 _Injected 的名字 → 这里把变体名字全试一遍。
    /// </summary>
    private static readonly string[] IcallProbeBase =
    {
        "UnityEngine.Sprite::Create",
        "UnityEngine.Sprite::CreateSprite",
        "UnityEngine.Sprite::Internal_CreateSprite",
        "UnityEngine.Sprite::get_texture",
        "UnityEngine.Sprite::get_rect",
        "UnityEngine.Sprite::get_bounds",
        "UnityEngine.Texture2D::LoadRawTextureData",
        "UnityEngine.Texture2D::SetPixelsImpl",
        "UnityEngine.Texture2D::ApplyImpl",
        "UnityEngine.Texture2D::GetRawTextureData",
        "UnityEngine.Texture2D::Internal_Create",
        "UnityEngine.ImageConversion::LoadImage",
        "UnityEngine.ImageConversion::EncodeToPNG",
        "UnityEngine.AudioClip::Construct_Internal",
        "UnityEngine.AudioClip::CreateUserSound",
        "UnityEngine.AudioClip::GetData",
        "UnityEngine.AudioClip::SetData",
        "UnityEngine.AudioClip::LoadAudioData",
        "UnityEngine.AudioClip::UnloadAudioData",
        "UnityEngine.AudioClip::GetName",
        "UnityEngine.Resources::FindObjectsOfTypeAll",
        "UnityEngine.Resources::Load",
        "UnityEngine.Object::FindObjectOfType",
        "UnityEngine.Mesh::Internal_Create",
        "UnityEngine.Shader::Find",
        "UnityEngine.Font::get_texture",
    };

    public static void ProbeMissingIcalls()
    {
        int total = 0, hit = 0;
        try
        {
            foreach (var b in IcallProbeBase)
            {
                var cands = new[]
                {
                    b,
                    b + "_Injected",
                    b + "Impl",
                    b + "ImplArray",
                    b + "Internal",
                    b.Replace("::", "::Internal_"),
                };
                foreach (var n in cands)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    total++;
                    IntPtr p;
                    try { p = RawTexture.ResolveIcall(n); }
                    catch { continue; }
                    if (p != IntPtr.Zero)
                    {
                        hit++;
                        MelonLogger.Warning("[ICALLPROBE] 命中 " + n + " = 0x" + p.ToInt64().ToString("X"));
                    }
                }
            }

            MelonLogger.Msg("[ICALLPROBE] 探测 " + total + " 个候选名（含 _Injected/Impl 变体），命中 " + hit + " 个");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[ICALLPROBE] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// 直接调**已注册**的 ICall <c>UnityEngine.ImageConversion::LoadImage</c> 把 PNG/JPG 字节塞进纹理。
    /// 托管 <c>ImageConversion.LoadImage</c> 不能走：真机实测它内部解析到不存在的名字 → SIGSEGV
    /// （和托管 Sprite.Create 同一个坑）。ABI：bool f(Texture2D* tex, Il2CppArray* data, bool markNonReadable)
    /// </summary>
    public static bool LoadImageViaIcall(IntPtr tex, byte[] data)
    {
        if (tex == IntPtr.Zero || data == null || data.Length == 0) return false;
        var fn = RawTexture.ResolveIcall("UnityEngine.ImageConversion::LoadImage");
        if (fn == IntPtr.Zero)
        {
            MelonLogger.Warning("[SPRITE] ImageConversion::LoadImage 未注册");
            return false;
        }

        var arr = NewByteArray(data);
        if (arr == IntPtr.Zero) return false;

        unsafe
        {
            var d = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, byte>)fn;
            var ok = d(tex, arr, 0);
            return ok != 0;
        }
    }

    /// <summary>构造 IL2CPP System.Byte[] 并填入数据（不依赖 RawTexture 的私有方法）。</summary>
    public static IntPtr NewByteArray(byte[] data)
    {
        try
        {
            var cls = IL2CPP.GetIl2CppClass("Il2Cppmscorlib.dll", "System", "Byte");
            if (cls == IntPtr.Zero) cls = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Byte");
            if (cls == IntPtr.Zero)
            {
                // 真机上 mscorlib 的 image 名不一定叫这两个，全量扫一遍
                unsafe
                {
                    var domain = IL2CPP.il2cpp_domain_get();
                    uint count = 0;
                    var asms = IL2CPP.il2cpp_domain_get_assemblies(domain, ref count);
                    for (uint i = 0; asms != null && i < count; i++)
                    {
                        var img = IL2CPP.il2cpp_assembly_get_image(*(asms + i));
                        if (img == IntPtr.Zero) continue;
                        cls = IL2CPP.il2cpp_class_from_name(img, "System", "Byte");
                        if (cls != IntPtr.Zero) break;
                    }
                }
            }

            if (cls == IntPtr.Zero)
            {
                MelonLogger.Warning("[SPRITE] 找不到 System.Byte 类");
                return IntPtr.Zero;
            }

            var arr = IL2CPP.il2cpp_array_new(cls, (ulong)data.Length);
            if (arr == IntPtr.Zero)
            {
                var arrCls = IL2CPP.il2cpp_array_class_get(cls, 1);
                if (arrCls != IntPtr.Zero) arr = IL2CPP.il2cpp_array_new_specific(arrCls, (ulong)data.Length);
            }

            if (arr == IntPtr.Zero) return IntPtr.Zero;
            System.Runtime.InteropServices.Marshal.Copy(data, 0, RawTexture.ArrayData(arr), data.Length);
            RawTexture.Root(arr);
            return arr;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[SPRITE] NewByteArray 失败: " + e.GetType().Name + " " + e.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 直接调**已注册**的 ICall <c>UnityEngine.Sprite::CreateSprite_Injected</c> 造精灵。
    /// 为什么不走托管 Sprite.Create：托管方法内部解析的是不存在的名字，真机上直接 SIGSEGV（已实测）。
    /// ABI（Unity Sprite.CreateSprite_Injected）：
    ///   Sprite* f(Texture2D* texture, Rect* rect, Vector2* pivot, float pixelsPerUnit,
    ///             uint extrude, int meshType, Vector4* border, bool generateFallbackPhysicsShape)
    /// </summary>
    public static IntPtr CreateSpriteViaIcall(IntPtr tex, float x, float y, float w, float h)
    {
        var fn = RawTexture.ResolveIcall("UnityEngine.Sprite::CreateSprite_Injected");
        if (fn == IntPtr.Zero)
        {
            MelonLogger.Warning("[SPRITE] CreateSprite_Injected 未注册，无法建精灵");
            return IntPtr.Zero;
        }

        unsafe
        {
            float* rect = stackalloc float[4];
            rect[0] = x;
            rect[1] = y;
            rect[2] = w;
            rect[3] = h;
            float* pivot = stackalloc float[2];
            pivot[0] = 0.5f;
            pivot[1] = 0.5f;
            float* border = stackalloc float[4];
            border[0] = border[1] = border[2] = border[3] = 0f;

            var d = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float, uint, int, float*, byte, IntPtr>)fn;
            var sprite = d(tex, rect, pivot, 100f, 0u, 0 /*FullRect*/, border, 0);
            if (sprite != IntPtr.Zero) RawTexture.Root(sprite);
            return sprite;
        }
    }

    /// <summary>读回 Sprite.texture（已注册 ICall）。</summary>
    public static IntPtr GetSpriteTextureViaIcall(IntPtr sprite)
    {
        var fn = RawTexture.ResolveIcall("UnityEngine.Sprite::get_texture");
        if (fn == IntPtr.Zero || sprite == IntPtr.Zero) return IntPtr.Zero;
        unsafe
        {
            var d = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)fn;
            return d(sprite);
        }
    }

    /// <summary>
    /// 【图标链路验证】独立判据：能不能真的造出 Texture2D + 上传像素 + 造出 Sprite。
    ///
    /// 背景（20:21 真机探测结论）：managed 侧解析的名字是缺的，但引擎其实注册了等价实现：
    ///   命中 UnityEngine.Sprite::CreateSprite_Injected = 0x...
    ///   命中 UnityEngine.Texture2D::SetPixelsImpl / ApplyImpl / Internal_CreateImpl
    ///   命中 UnityEngine.ImageConversion::LoadImage / EncodeToPNG
    ///   命中 UnityEngine.Sprite::get_texture / get_rect_Injected / get_bounds_Injected
    /// 所以「造精灵」这条路是通的，之前只是探测了 3 个不存在的名字就整体跳过了。
    /// 本方法全程只用 IntPtr（绝不包装托管 Texture2D），并打印：
    ///   Texture2D 指针 / Color32[] 指针 / SetPixels32 结果 / Apply 结果 / Sprite 指针 / sprite.texture 读回值
    /// </summary>
    public static void TestSpritePipeline()
    {
        try
        {
            MelonLogger.Msg("[SPRITETEST] ===== 开始 =====");
            var tex = RawTexture.CreateTexture2D(8, 8, 4 /*RGBA32*/, false);
            MelonLogger.Msg("[SPRITETEST] Texture2D(8x8 RGBA32) = 0x" + tex.ToInt64().ToString("X")
                            + (tex == IntPtr.Zero ? " [" + RawTexture.LastError + "]" : ""));
            if (tex == IntPtr.Zero)
            {
                MelonLogger.Msg("[SPRITETEST] ===== 结束（纹理创建失败）=====");
                return;
            }

            var okApply = RawTextureRaw.Apply(tex, false, false);
            MelonLogger.Msg("[SPRITETEST] Apply = " + okApply + (okApply ? "" : " [" + RawTextureRaw.LastError + "]"));

            // 直接调已注册的 ICall 建精灵（托管 Sprite.Create 会 SIGSEGV，不再走）
            var sprite = CreateSpriteViaIcall(tex, 0, 0, 8, 8);
            MelonLogger.Msg("[SPRITETEST] ICall CreateSprite_Injected -> 0x" + sprite.ToInt64().ToString("X"));

            var back = GetSpriteTextureViaIcall(sprite);
            MelonLogger.Msg("[SPRITETEST] sprite.texture 读回 = 0x" + back.ToInt64().ToString("X")
                            + "（期望 0x" + tex.ToInt64().ToString("X") + "）"
                            + (back == tex && tex != IntPtr.Zero ? "  ✓ 一致" : "  ✗ 不一致"));

            // 记录到字典，供后续图标管线复用
            if (sprite != IntPtr.Zero && tex != IntPtr.Zero)
                MelonLogger.Msg("[SPRITETEST] 判定：Sprite 链路可用 ✓");
            else
                MelonLogger.Msg("[SPRITETEST] 判定：Sprite 链路仍不可用 ✗");

            // ---- 像素上传验证：managed 编 PNG → ImageConversion.LoadImage ----
            var rgba = new byte[8 * 8 * 4];
            for (var i = 0; i < 64; i++)
            {
                rgba[i * 4 + 0] = 255;
                rgba[i * 4 + 3] = 255;
            }

            var png = IconPack.EncodePng(rgba, 8, 8);
            MelonLogger.Msg("[SPRITETEST] PNG 编码 = " + (png?.Length ?? -1) + " 字节"
                            + (png == null ? " [" + IconPack.LastError + "]" : ""));
            var tex2 = RawTexture.CreateTexture2D(8, 8, 4, false);
            MelonLogger.Msg("[SPRITETEST] 第 2 张纹理 = 0x" + tex2.ToInt64().ToString("X"));
            var okLoad = LoadImageViaIcall(tex2, png);
            MelonLogger.Msg("[SPRITETEST] ICall ImageConversion::LoadImage = " + okLoad);
            var sprite2 = CreateSpriteViaIcall(tex2, 0, 0, 8, 8);
            var back2 = GetSpriteTextureViaIcall(sprite2);
            MelonLogger.Msg("[SPRITETEST] 带像素 sprite = 0x" + sprite2.ToInt64().ToString("X")
                            + " 纹理回读 = 0x" + back2.ToInt64().ToString("X")
                            + (back2 == tex2 && tex2 != IntPtr.Zero ? "  ✓" : "  ✗"));

            MelonLogger.Msg("[SPRITETEST] ===== 结束 =====");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[SPRITETEST] 异常: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// 音频 ICall 变体探测（Windy 的 wav 目前全部解码失败：Construct_Internal 未注册）。
    /// 复用「名字对不上」这条经验：managed 侧名字缺失，但可能出现 _Injected/Impl 变体。
    /// </summary>
    private static readonly string[] AudioIcallProbeBase =
    {
        "UnityEngine.AudioClip::Construct_Internal",
        "UnityEngine.AudioClip::Construct",
        "UnityEngine.AudioClip::CreateUserSound",
        "UnityEngine.AudioClip::GetData",
        "UnityEngine.AudioClip::SetData",
        "UnityEngine.AudioClip::GetName",
        "UnityEngine.AudioClip::LoadAudioData",
        "UnityEngine.AudioClip::UnloadAudioData",
        "UnityEngine.AudioClip::Init_Internal",
        "UnityEngine.AudioClip::Init",
        "UnityEngine.AudioClip::Create",
        "UnityEngine.AudioClip::get_samples",
        "UnityEngine.AudioClip::get_channels",
        "UnityEngine.AudioClip::get_frequency",
        "UnityEngine.AudioSource::Play",
        "UnityEngine.AudioSource::PlayOneShot",
        "UnityEngine.AudioSource::set_clip",
        "UnityEngine.AudioSource::get_clip",
        "UnityEngine.AudioSource::set_volume",
    };

    public static void ProbeAudioIcalls()
    {
        int total = 0, hit = 0;
        try
        {
            foreach (var b in AudioIcallProbeBase)
            {
                var cands = new[]
                {
                    b,
                    b + "_Injected",
                    b + "Impl",
                    b + "ImplArray",
                    b + "Internal",
                    b.Replace("::", "::Internal_"),
                };
                foreach (var n in cands)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    total++;
                    IntPtr p;
                    try { p = RawTexture.ResolveIcall(n); }
                    catch { continue; }
                    if (p != IntPtr.Zero)
                    {
                        hit++;
                        MelonLogger.Warning("[AUDIOPROBE] 命中 " + n + " = 0x" + p.ToInt64().ToString("X"));
                    }
                }
            }

            MelonLogger.Msg("[AUDIOPROBE] 探测 " + total + " 个音频候选名，命中 " + hit + " 个");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[AUDIOPROBE] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// 【回归修复】克隆出来的 mod 对象与模板**共享引用类型子对象**（Object::Internal_CloneSingle 是浅拷贝）。
    /// 之后不管是我们 warp 还是 JsonUtility.FromJsonOverwrite，写这些子对象都会**改到游戏自带数据**，
    /// 于是游戏的事件/天气数据被污染（用户实测：开局事件选「请给我药物」没反应、改天气无效）。
    ///
    /// 处理原则：
    ///   · **纯托管类**子对象（LocalizedString / CardAction / DurabilityStat…）：克隆一份私有副本再写回字段
    ///     —— 这类对象是「数据载体」，复制语义正确，之后所有写入都落在副本上；
    ///   · **UnityEngine.Object 派生**（Sprite / AudioClip / CardData / Gamemode…）：**绝不复制**，
    ///     它们是资产引用，身份必须保持；mod 数据通过 WarpData 引用解析把真实指针写进字段，不会改到资产本身；
    ///   · 数组 / List：warp 是**整体替换成新集合**（SetArrByWarpper 新建数组），不原地改，安全。
    /// </summary>
    public static void DetachPlainClassChildren(ScriptableObject obj, string tag)
    {
        if (obj == null) return;
        try
        {
            var gen = WarpperClassGen.MainGen.GetOrGen(obj.GetType());
            int detached = 0, failed = 0;
            foreach (var kv in gen)
            {
                var fldType = kv.Value.fldType;
                if (fldType == null) continue;
                if (fldType.IsValueType) continue;
                if (typeof(UnityEngine.Object).IsAssignableFrom(fldType)) continue;   // 资产引用：不复制
                if (fldType.IsArray || fldType.IsGenericType) continue;               // 集合：由 warp 整体替换

                try
                {
                    var cur = WarpperClassGen.MainGenTools.CommonGet(obj, kv.Key);
                    if (cur is not Il2CppObjectBase cob) continue;
                    var childPtr = IL2CPP.Il2CppObjectBaseToPtr(cob);
                    if (childPtr == IntPtr.Zero) continue;

                    var cls = IL2CPP.il2cpp_object_get_class(childPtr);
                    if (cls == IntPtr.Zero) continue;
                    var clsName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
                    if (clsName != fldType.Name) continue;      // 类型与生成表不符就不动它

                    var copy = CloneSingle(childPtr);
                    if (copy == IntPtr.Zero)
                    {
                        failed++;
                        continue;
                    }

                    var wrapped = Activator.CreateInstance(fldType, new object[] { copy });
                    WarpperClassGen.MainGenTools.CommonSetFld(obj, kv.Key, wrapped);
                    detached++;
                }
                catch
                {
                    failed++;
                }
            }

            if (detached > 0 || failed > 0)
                MelonLogger.Msg("[DETACH] " + tag + " " + obj.GetType().Name + " 断开共享子对象=" + detached
                                + (failed > 0 ? " 失败=" + failed : ""));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DETACH] " + tag + " 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    private static IntPtr _cloneSingleFn;

    /// <summary>调已注册 ICall Object::Internal_CloneSingle（返回新原生对象指针）。</summary>
    public static IntPtr CloneSingle(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        if (_cloneSingleFn == IntPtr.Zero)
            _cloneSingleFn = RawTexture.ResolveIcall("UnityEngine.Object::Internal_CloneSingle");
        if (_cloneSingleFn == IntPtr.Zero) return IntPtr.Zero;
        unsafe
        {
            var d = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_cloneSingleFn;
            return d(obj);
        }
    }

    /// <summary>
    /// 【回归根治】克隆体「去共享」：把克隆出来的 mod 对象身上所有**引用类型数据字段**
    /// （数组 / Il2CppReferenceArray / List&lt;T&gt; / Dictionary / string / 纯托管类子对象）
    /// 就地换成**全新的空实例**；之后再跑 FromJsonOverwrite / warp / FillDropsList，
    /// 写入就只落在 mod 自己的对象上，绝不碰游戏资产。
    ///
    /// 原则（按 Lead 指定 + 上次 SIGSEGV 的教训）：
    ///   · 只新建**空容器 / 空实例**，**绝不克隆元素**（Internal_CloneSingle 对纯托管类会 SIGSEGV）；
    ///   · UnityEngine.Object 派生的字段（Sprite / AudioClip / CardData / Gamemode…）是**资产引用**，保持不动
    ///     —— 它们的真实指针由 WarpData 引用解析写入；
    ///   · 值类型（int/float/struct/enum）字段保持不动。
    /// </summary>
    public static int NeutralizeClone(ScriptableObject clone, string tag)
    {
        if (clone == null) return 0;
        var stats = new int[6];   // 0数组 1列表 2字典 3字符串 4子对象 5失败
        try
        {
            DeepDetach(clone, tag, 0, stats);
            var total = stats[0] + stats[1] + stats[2] + stats[3] + stats[4];
            if (total > 0 || stats[5] > 0)
                MelonLogger.Msg("[NEUTRAL] " + tag + " " + clone.GetType().Name
                                + " 新建私有容器/实例=" + total + "（数组 " + stats[0] + " 列表 " + stats[1]
                                + " 字典 " + stats[2] + " 字符串 " + stats[3] + " 子对象 " + stats[4] + "）"
                                + (stats[5] > 0 ? " 失败=" + stats[5] : ""));
            return total;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[NEUTRAL] " + tag + " 失败: " + e.GetType().Name + " " + e.Message);
            return 0;
        }
    }

    /// <summary>
    /// 递归「去共享」（就地改成 mod 私有副本）：
    ///   · 数组 → **等长**新数组，每个元素换成**同类型新实例**（递归处理）；元素是 UnityEngine.Object 资产则保持共享；
    ///   · List/Dictionary → 新建空容器（元素由 JSON/warp 填充）；
    ///   · string → null；纯托管类子对象 → il2cpp_object_new 同类型新实例并递归。
    /// 与上一版"清空成 0 长度"的关键差别：**数组保长度、元素建对象**，
    /// 这样嵌套 warp（ProducedCards 之类的 WarpData 引用）才有对象可写 —— 这正是
    /// 事件选项「请给我药物/改变天气/压缩肉干」没产出的根因（DismantleActions 被清空后无处回填）。
    /// </summary>
    private static void DeepDetach(object obj, string tag, int depth, int[] stats)
    {
        // 深度上限放宽到 16（带环保护：新建的子对象字段初始为 null，不会回指源对象）
        if (obj == null || depth > 16) return;
        try
        {
            var gen = WarpperClassGen.MainGen.GetOrGen(obj.GetType());
            var objHandle = IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)obj);

            foreach (var kv in gen)
            {
                var t = kv.Value.fldType;
                if (t == null || t.IsValueType) continue;
                if (typeof(UnityEngine.Object).IsAssignableFrom(t)) continue;   // 资产引用：保持共享

                try
                {
                    if (t == typeof(string))
                    {
                        IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + kv.Value.fOffset, IntPtr.Zero);
                        stats[3]++;
                        continue;
                    }

                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>))
                    {
                        var closed = typeof(Il2CppSystem.Collections.Generic.List<>)
                            .MakeGenericType(t.GetGenericArguments()[0]);
                        WarpperClassGen.MainGenTools.CommonSetFld(obj, kv.Key, Activator.CreateInstance(closed));
                        stats[1]++;
                        continue;
                    }

                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.Dictionary<,>))
                    {
                        var ga = t.GetGenericArguments();
                        var closed = typeof(Il2CppSystem.Collections.Generic.Dictionary<,>)
                            .MakeGenericType(ga[0], ga[1]);
                        WarpperClassGen.MainGenTools.CommonSetFld(obj, kv.Key, Activator.CreateInstance(closed));
                        stats[2]++;
                        continue;
                    }

                    if (t.IsGenericType && IsIl2CppArrayType(t))
                    {
                        var elemType = t.GetGenericArguments()[0];
                        var elemCls = NativeClassOf(elemType);
                        if (elemCls == IntPtr.Zero)
                        {
                            stats[5]++;
                            continue;
                        }

                        var old = WarpperClassGen.MainGenTools.CommonGet(obj, kv.Key);
                        var n = (int)ElemCount(old);
                        var arr = IL2CPP.il2cpp_array_new(elemCls, (ulong)n);
                        if (arr == IntPtr.Zero)
                        {
                            stats[5]++;
                            continue;
                        }

                        var elemIsAsset = typeof(UnityEngine.Object).IsAssignableFrom(elemType);
                        var header = IntPtr.Size == 8 ? 0x20 : 0x10;
                        for (var i = 0; i < n; i++)
                        {
                            var oldElem = GetElem(old, i);
                            IntPtr newElem = IntPtr.Zero;
                            if (oldElem == null)
                            {
                                newElem = IntPtr.Zero;
                            }
                            else if (elemIsAsset)
                            {
                                newElem = IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)oldElem);
                            }
                            else
                            {
                                newElem = IL2CPP.il2cpp_object_new(elemCls);
                                if (newElem != IntPtr.Zero)
                                {
                                    var wrapped = Activator.CreateInstance(elemType, new object[] { newElem });
                                    DeepDetach(wrapped, tag, depth + 1, stats);   // 递归
                                }
                            }

                            IL2CPP.il2cpp_gc_wbarrier_set_field(arr, arr + header + i * IntPtr.Size, newElem);
                        }

                        IL2CPP.il2cpp_gc_wbarrier_set_field(objHandle, objHandle + kv.Value.fOffset, arr);
                        stats[0]++;
                        continue;
                    }

                    // 纯托管类子对象：同类型新实例 + 递归
                    var cls = IL2CPP.il2cpp_class_from_il2cpp_type(IL2CPP.il2cpp_field_get_type(kv.Value.fPtr));
                    if (cls == IntPtr.Zero)
                    {
                        stats[5]++;
                        continue;
                    }

                    var newPtr = IL2CPP.il2cpp_object_new(cls);
                    if (newPtr == IntPtr.Zero)
                    {
                        stats[5]++;
                        continue;
                    }

                    var wrappedNew = Activator.CreateInstance(t, new object[] { newPtr });
                    WarpperClassGen.MainGenTools.CommonSetFld(obj, kv.Key, wrappedNew);
                    DeepDetach(wrappedNew, tag, depth + 1, stats);
                    stats[4]++;
                    continue;
                }
                catch
                {
                    stats[5]++;
                }
            }
        }
        catch
        {
            stats[5]++;
        }
    }

    /// <summary>反射取容器第 i 个元素（数组用 Length/索引器，List 用 Count/索引器）。</summary>
    public static object GetElem(object container, int i)
    {
        if (container == null) return null;
        try
        {
            var t = container.GetType();
            var idx = t.GetProperty("Item", new[] { typeof(int) });
            return idx?.GetValue(container, new object[] { i });
        }
        catch
        {
            return null;
        }
    }

    public static bool IsIl2CppArrayTypePublic(Type t) => IsIl2CppArrayType(t);

    /// <summary>反射设置容器第 i 个元素。</summary>
    public static bool SetElem(object container, int i, object val)
    {
        try
        {
            var idx = container.GetType().GetProperty("Item", new[] { typeof(int) });
            if (idx == null || !idx.CanWrite) return false;
            idx.SetValue(container, val, new object[] { i });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIl2CppArrayType(Type t)
    {
        try
        {
            var bt = t.GetGenericTypeDefinition().BaseType;
            while (bt != null)
            {
                if (bt.IsGenericType &&
                    bt.GetGenericTypeDefinition() ==
                    typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<>))
                    return true;
                bt = bt.BaseType;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return false;
    }

    /// <summary>取 interop 代理类型对应的原生 Il2CppClass*。</summary>
    public static IntPtr NativeClassOfPublic(Type t) => NativeClassOf(t);

    private static readonly Dictionary<string, IntPtr> ClassPtrByNameCache = new();

    /// <summary>找同类的**现成实例指针**（模板克隆用）—— 从游戏注册表里挑第一个同类对象。</summary>
    public static IntPtr FindTemplatePtrByClassName(string clsName)
    {
        if (string.IsNullOrEmpty(clsName)) return IntPtr.Zero;
        if (TemplatePtrCache.TryGetValue(clsName, out var cached)) return cached;
        var found = IntPtr.Zero;
        try
        {
            var reg = UniqueIDScriptable.AllUniqueObjects;
            if (reg != null)
                foreach (var kv in reg)
                {
                    var o = kv.Value;
                    if (o == null) continue;
                    var pp = IL2CPP.Il2CppObjectBaseToPtr(o);
                    if (pp == IntPtr.Zero) continue;
                    if (Cls(pp) != clsName) continue;
                    found = pp;
                    break;
                }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        TemplatePtrCache[clsName] = found;
        return found;
    }

    private static readonly Dictionary<string, IntPtr> TemplatePtrCache = new();
    /// <summary>退路：从同类的现成实例借 il2cpp 类（例如注册表里就有该类型的对象时）。</summary>
    public static IntPtr FindClassPtrByClassName(string clsName)
    {
        if (string.IsNullOrEmpty(clsName)) return IntPtr.Zero;
        if (ClassPtrByNameCache.TryGetValue(clsName, out var cached)) return cached;
        var found = IntPtr.Zero;
        try
        {
            var reg = UniqueIDScriptable.AllUniqueObjects;
            if (reg != null)
                foreach (var kv in reg)
                {
                    var o = kv.Value;
                    if (o == null) continue;
                    var pp = IL2CPP.Il2CppObjectBaseToPtr(o);
                    if (pp == IntPtr.Zero) continue;
                    if (Cls(pp) != clsName) continue;
                    found = IL2CPP.il2cpp_object_get_class(pp);
                    break;
                }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
        ClassPtrByNameCache[clsName] = found;
        return found;
    }

    private static IntPtr NativeClassOf(Type interopType)
    {
        try
        {
            var store = typeof(Il2CppClassPointerStore<>).MakeGenericType(interopType);
            var f = store.GetField("NativeClassPtr",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (f != null)
            {
                var v = (IntPtr)f.GetValue(null);
                if (v != IntPtr.Zero) return v;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return IntPtr.Zero;
    }

    // ================== 游戏数据不变性判据（Lead 指定）==================
    // 目标：warp 前后「游戏自带对象的集合尺寸」完全不变。

    private static readonly string[] WitnessTypes = { "CardData", "Encounter", "CharacterPerk", "GameStat" };

    public static Dictionary<string, (long Size, long Content, string Detail)> SnapshotGameContainers(int maxPerType)
    {
        var snap = new Dictionary<string, (long, long, string)>();
        foreach (var cls in WitnessTypes)
        {
            foreach (var kv in RegistryByClass(cls, maxPerType))
            {
                SigOf(cls, kv.Value, out var size, out var content, out var detail);
                if (size >= 0) snap[cls + "|" + kv.Key] = (size, content, detail);
            }
        }

        MelonLogger.Msg("[INVARIANT] 采样游戏自带对象 " + snap.Count + " 个（每类最多 " + maxPerType
                        + "：" + string.Join("/", WitnessTypes) + "），记录 集合尺寸 + 内容哈希 + 掉落/动作字段");
        return snap;
    }

    public static void CompareGameContainers(Dictionary<string, (long Size, long Content, string Detail)> before, string tag)
    {
        if (before == null || before.Count == 0) return;
        int sizeChanged = 0, contentChanged = 0, lost = 0;
        int skippedIntentional = 0;
        long sumBefore = 0, sumAfter = 0;
        var samples = new System.Collections.Generic.List<string>();
        foreach (var kv in before)
        {
            sumBefore += kv.Value.Size;
            var parts = kv.Key.Split('|');

            // [2026-10-03] GSM 有意改造的游戏卡牌属于"有意的数据变更"，从污染判据排除，
            // 由 [GSM] 清单单独列出 —— 这样"有意改动"与"意外污染"分得清。
            if (parts.Length > 1 && IntentionalGuids.Contains(parts[1]))
            {
                skippedIntentional++;
                GsmSampleHit++;
                continue;
            }

            var ok = false;
            long size = -1, content = -1;
            string detail = null;
            try
            {
                if (UniqueIDScriptable.AllUniqueObjects.TryGetValue(parts[1], out var obj) && obj != null)
                {
                    SigOf(parts[0], obj, out size, out content, out detail);
                    ok = size >= 0;
                }
            }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

            if (!ok)
            {
                lost++;
                if (samples.Count < 6) samples.Add(parts[0] + ":" + parts[1].Substring(0, 8) + " 丢失");
                continue;
            }

            sumAfter += size;
            if (size != kv.Value.Size)
            {
                sizeChanged++;
                if (samples.Count < 6)
                    samples.Add(parts[0] + ":" + parts[1].Substring(0, 8) + " 尺寸 " + kv.Value.Size + "→" + size);
            }

            if (content != kv.Value.Content)
            {
                contentChanged++;
                if (samples.Count < 6)
                    samples.Add(parts[0] + ":" + parts[1].Substring(0, 8) + " 内容差异: " + DiffDetail(kv.Value.Detail, detail));
            }
        }

        MelonLogger.Msg("[INVARIANT] " + tag + " 游戏对象（尺寸/内容/丢失）: 尺寸变化=" + sizeChanged
                        + " 内容变化=" + contentChanged + " 丢失=" + lost + " / 共 " + before.Count
                        + "（尺寸总和 " + sumBefore + "→" + sumAfter + "）"
                        + "；GSM 有意修改目标总数=" + IntentionalGuids.Count + "，其中落在本次采样内=" + skippedIntentional + "（已排除）"
                        + ((sizeChanged + contentChanged + lost) == 0
                            ? " ✓ 尺寸与内容均完全不变"
                            : " 样本: " + string.Join(" | ", samples)));
    }

    /// <summary>按真实类名把注册表对象转成具体代理，产出 (集合尺寸总和, 内容哈希, 掉落/动作字段明细)。</summary>
    private static void SigOf(string clsName, UniqueIDScriptable o, out long size, out long content, out string detail)
    {
        size = -1;
        content = -1;
        detail = null;
        try
        {
            switch (clsName)
            {
                case "CardData": SigOfTyped(CastOrNull<CardData>(o), typeof(CardData), out size, out content, out detail); break;
                case "Encounter": SigOfTyped(CastOrNull<Encounter>(o), typeof(Encounter), out size, out content, out detail); break;
                case "CharacterPerk": SigOfTyped(CastOrNull<CharacterPerk>(o), typeof(CharacterPerk), out size, out content, out detail); break;
                case "GameStat": SigOfTyped(CastOrNull<GameStat>(o), typeof(GameStat), out size, out content, out detail); break;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>
    /// 内容签名：对每个「数组/List」字段累计 尺寸 + 首元素身份（对象取指针、字符串取哈希），
    /// 这样不仅"尺寸不变"，"内容被换成别的对象"也能被发现。
    /// 另外把名字里含 Drop / Loot / Action / Card 的字段明细打出来，便于人眼核对。
    /// </summary>
    private static void SigOfTyped(object o, Type t, out long size, out long content, out string detail)
    {
        size = -1;
        content = -1;
        detail = null;
        if (o == null) return;

        long sz = 0, ct = 17;
        var parts = new System.Collections.Generic.List<string>();
        var gen = WarpperClassGen.MainGen.GetOrGen(t);
        foreach (var kv in gen)
        {
            var ft = kv.Value.fldType;
            if (ft == null || !ft.IsGenericType) continue;
            var isList = false;
            try { isList = ft.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>); }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            if (!isList && !IsIl2CppArrayType(ft)) continue;

            try
            {
                var v = WarpperClassGen.MainGenTools.CommonGet(o, kv.Key);
                var n = ElemCount(v);
                var id = FirstElemIdentity(v);
                sz += n;
                ct = ct * 31 + (n * 7919L + id);
                // 每个容器字段一条明细（含 field=数量/首元素身份），用于精确 diff
                parts.Add(kv.Key + "=" + n + "/0x" + id.ToString("X"));
            }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
        }

        size = sz;
        content = ct;
        detail = string.Join(" ", parts);
    }

    /// <summary>
    /// 首元素身份。注意：**装箱的值类型元素**每次读取都会得到新的托管包装对象，
    /// 其 Pointer 不稳定（会造成内容哈希假阳性），所以对值类型元素改用内容哈希。
    /// </summary>
    private static long FirstElemIdentity(object v)
    {
        var n = ElemCount(v);
        if (n <= 0 || v == null) return 0;
        try
        {
            var vt = v.GetType();
            var idx = vt.GetProperty("Item", new[] { typeof(int) });
            if (idx == null) return 0;
            var e = idx.GetValue(v, new object[] { 0 });
            if (e == null) return 0;
            if (e is string s) return s.GetHashCode();
            if (e is Il2CppObjectBase cob)
            {
                var ptr = cob.Pointer;
                if (ptr == IntPtr.Zero) return 0;
                var cls = IL2CPP.il2cpp_object_get_class(ptr);
                if (cls != IntPtr.Zero && IL2CPP.il2cpp_class_is_valuetype(cls))
                    return e.ToString()?.GetHashCode() ?? 0;      // 装箱值类型：用内容，不用指针
                return ptr.ToInt64();
            }

            return e.ToString()?.GetHashCode() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>比较两份字段明细，返回不同项（限制条数）。</summary>
    private static string DiffDetail(string before, string after)
    {
        try
        {
            var b = new Dictionary<string, string>();
            foreach (var s in (before ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = s.IndexOf('=');
                if (i > 0) b[s.Substring(0, i)] = s.Substring(i + 1);
            }

            var a = new Dictionary<string, string>();
            foreach (var s in (after ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = s.IndexOf('=');
                if (i > 0) a[s.Substring(0, i)] = s.Substring(i + 1);
            }

            var diffs = new System.Collections.Generic.List<string>();
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var oldVal)) diffs.Add(kv.Key + "=新增" + kv.Value);
                else if (oldVal != kv.Value) diffs.Add(kv.Key + " " + oldVal + "→" + kv.Value);
                if (diffs.Count >= 6) break;
            }

            return diffs.Count == 0 ? "(字段明细无差异)" : string.Join(", ", diffs);
        }
        catch
        {
            return "(diff 失败)";
        }
    }

    /// <summary>数组元素 warp 的抽样计数。</summary>
    public static int ArrWarpTrace;
    /// <summary>引用解析抽样计数。</summary>
    public static int ResolveTrace;

    public static long ElemCount(object v)
    {
        if (v == null) return 0;
        try
        {
            var vt = v.GetType();
            var p = vt.GetProperty("Length") ?? vt.GetProperty("Count");
            return p != null ? Convert.ToInt64(p.GetValue(v)) : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>按真实类名把注册表对象转成具体代理，再用 gen 表把所有「数组/List」字段的尺寸求和。</summary>
    private static long SignatureOf(string clsName, UniqueIDScriptable o)
    {
        SigOf(clsName, o, out var size, out _, out _);
        return size;
    }

    private static long ContainerSum(object o, Type t)
    {
        if (o == null) return -1;
        long sum = 0;
        var gen = WarpperClassGen.MainGen.GetOrGen(t);
        foreach (var kv in gen)
        {
            var ft = kv.Value.fldType;
            if (ft == null || !ft.IsGenericType) continue;
            var isList = false;
            try { isList = ft.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>); }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            if (!isList && !IsIl2CppArrayType(ft)) continue;

            try
            {
                var v = WarpperClassGen.MainGenTools.CommonGet(o, kv.Key);
                sum += ElemCount(v);
            }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
        }

        return sum;
    }

    /// <summary>
    /// 【正向判据】把某张 mod 卡的"效果链"打出来：数组字段的条数 → 每个元素的
    /// ActionName.DefaultText → 其 ProducedCards[0] 的 CollectionName 与**解析后的对象指针**。
    /// 用来证明「事件选项真的有产出集合」，而不是"空的没报错"。
    /// 全部走反射/属性，不依赖编译期类型。
    /// </summary>
    public static void DumpEffectArray(object card, string field, string tag)
    {
        if (!MiniLoader.DiagFull) { EventCardDumps++; return; }
        try
        {
            var container = WarpperClassGen.MainGenTools.CommonGet(card, field);
            var n = (int)ElemCount(container);
            MelonLogger.Msg("[EFFECT] " + tag + "." + field + " 条数=" + n);
            if (n <= 0) return;

            for (var i = 0; i < n && i < 8; i++)
            {
                var elem = GetElem(container, i);
                if (elem == null)
                {
                    MelonLogger.Msg("[EFFECT]   [" + i + "] <null>");
                    continue;
                }

                if (i == 0)
                {
                    var keys = new System.Collections.Generic.List<string>();
                    foreach (var kv in WarpperClassGen.MainGen.GetOrGen(elem.GetType()))
                        keys.Add(kv.Key + ":" + (kv.Value.fldType?.Name ?? "?"));
                    MelonLogger.Msg("[EFFECT]   元素类型=" + elem.GetType().Name + " 字段(" + keys.Count + "): "
                                    + string.Join(", ", keys));
                }

                var actionName = Member(elem, "ActionName");
                var txt = Member(actionName, "DefaultText")?.ToString();
                var produced = Member(elem, "ProducedCards");
                var pn = (int)ElemCount(produced);
                var first = pn > 0 ? GetElem(produced, 0) : null;
                string cname = null;
                var cptr = IntPtr.Zero;
                if (first != null)
                {
                    cname = Member(first, "CollectionName")?.ToString();
                    cptr = PtrOf(first);
                }

                MelonLogger.Msg("[EFFECT]   [" + i + "] ActionName=\"" + (txt ?? "<null>") + "\" ProducedCards="
                                + pn + " 首个集合名=" + (cname ?? "<null>") + " 指针=0x"
                                + cptr.ToInt64().ToString("X"));

                // [EFFECT2] 再深一层：集合里的 DroppedCards[0].DroppedCard（真正要产出的那张卡）
                if (first != null)
                {
                    var dropped = Member(first, "DroppedCards");
                    var dn = (int)ElemCount(dropped);
                    var line = "[EFFECT2] 选项\"" + (txt ?? "?") + "\" → ProducedCards[0].DroppedCards 长度=" + dn;
                    for (var j = 0; j < dn && j < 4; j++)
                    {
                        var dc = GetElem(dropped, j);
                        if (dc == null)
                        {
                            line += "; [" + j + "]=<null>";
                            continue;
                        }

                        var cardObj = Member(dc, "DroppedCard");
                        var qty = Member(dc, "Quantity");
                        var qs = "<null>";
                        try
                        {
                            if (qty != null) qs = "(" + Member(qty, "x") + "," + Member(qty, "y") + ")";
                        }
                        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

                        line += "; [" + j + "] DroppedCard=" + (cardObj == null
                            ? "<null>"
                            : NameOf(cardObj) + "/0x" + PtrOf(cardObj).ToInt64().ToString("X"))
                                + " Quantity=" + qs;
                    }

                    MelonLogger.Msg(line);
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[EFFECT] " + tag + " 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>打一个「命名字符串数组」字段（例如 CardTags），直接给名字，便于验收"非空且名字正确"。</summary>
    public static void DumpNamedArray(object obj, string field, string tag)
    {
        if (!MiniLoader.DiagFull) { EventCardDumps++; return; }
        try
        {
            var c = WarpperClassGen.MainGenTools.CommonGet(obj, field);
            var n = (int)ElemCount(c);
            var names = new System.Collections.Generic.List<string>();
            for (var i = 0; i < n && i < 20; i++)
                names.Add(Member(GetElem(c, i), "name")?.ToString() ?? "?");
            MelonLogger.Msg("[TAGS] " + tag + "." + field + " = " + n + " 项"
                            + (n > 0 ? ": " + string.Join(", ", names) : ""));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[TAGS] " + tag + "." + field + " 失败: " + e.Message);
        }
    }

    /// <summary>先按属性取，取不到再按 gen 表字段取。</summary>
    public static object Member(object o, string name)
    {
        if (o == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var p = o.GetType().GetProperty(name);
            if (p != null) return p.GetValue(o);
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        try
        {
            return WarpperClassGen.MainGenTools.CommonGet(o, name);
        }
        catch
        {
            return null;
        }
    }

    public static IntPtr PtrOf(object o)
    {
        try
        {
            return o is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 【关键补漏】把容器里取出来的元素**按 il2cpp 真实类名重新包装成具体代理类型**。
    ///
    /// 现象（真机 21:12 实证）：从 `DismantleActions` 数组取出的元素包装类型是 `Il2CppSystem.Object`，
    /// 于是 `MainGen.GetOrGen(Object)` 的 gen 表为空 → 递归 warp 全被跳过
    /// （日志：`[CHAIN] 对象字段 Object.ActionName` → `↳ 字段不在 gen 表`）→ 5 层引用链断在这里，
    /// 最内层 `DroppedCard` 永远是 null。按 il2cpp 原生类名重建同类型代理后，
    /// `GetOrGen` 就能拿到该类的全部（含继承）字段，链子才走得下去。
    /// </summary>
    public static object Retype(object o)
    {
        if (o == null) return null;
        try
        {
            if (o is not Il2CppObjectBase b) return o;
            var ptr = b.Pointer;
            if (ptr == IntPtr.Zero) return o;
            var clsName = Cls(ptr);
            if (string.IsNullOrEmpty(clsName) || clsName[0] == '<') return o;
            var cur = o.GetType();
            if (cur.Name == clsName) return o;
            var t = FindTypeByName(clsName);
            if (t == null || t == cur) return o;
            return Activator.CreateInstance(t, new object[] { ptr });
        }
        catch
        {
            return o;
        }
    }

    private static readonly Dictionary<string, Type> TypeByNameCache = new();

    // ───────────────────────── C：按「类型 + 名字」解析原版资产 ─────────────────────────
    // 背景：原版资产（CardTag / EquipmentTag / AudioClip / Sprite …）在 mod JSON 里是按
    // **名字**引用的（如 "tag_Decoration"），而我们的注册表只有 GUID 键（AllUniqueObjects），
    // 且这些类型**不派生自 UniqueIDScriptable** → 按 GUID 永远解析不到（CardTags 会被写成空数组）。
    // 做法：从注册表里那 2858 个对象出发，按字段类型名筛出目标类型的引用，收集它们并按 `.name` 建索引。
    private static readonly Dictionary<string, Dictionary<string, object>> NameIndex = new();
    private static readonly Dictionary<string, int> NameIndexConflicts = new();
    private static bool AllIndexesBuilt;

    /// <summary>一次遍历就同时建这些类型的索引（避免"每类型各扫一遍 2858 对象"→ 每类型 4 秒）。</summary>
    private static readonly string[] WantedIndexTypes =
    {
        "CardTag", "EquipmentTag", "ActionTag", "DamageType", "Sprite", "AudioClip",
        "CardData", "CardTabGroup", "WeatherSpecialEffect", "GameStat", "Encounter",
        "CharacterPerk", "ScriptableObject", "GameObject", "MonoBehaviour"
    };

    private static readonly Dictionary<Type, bool> ClassCandidateCache = new();

    /// <summary>
    /// 单遍建索引：遍历注册表对象一次，同时为所有 `WantedIndexTypes` 建「名字→对象」索引。
    /// 两个关键优化（上一版每类型 4 秒、12 个类型 >70 秒）：
    ///   ① 按**类**预筛：类里没有任何候选字段的对象直接跳过（大多数类属于此类）；
    ///   ② 只读候选字段（元素类型名命中目标，或属于可下探的容器类）。
    /// </summary>
    public static void EnsureAllNameIndexes()
    {
        if (AllIndexesBuilt) return;
        AllIndexesBuilt = true;
        foreach (var t in WantedIndexTypes)
            if (!NameIndex.ContainsKey(t))
                NameIndex[t] = new Dictionary<string, object>();

        NameIndexBudget = 400000;
        var scanned = 0;
        var skipped = 0;
        var t0 = Environment.TickCount;
        try
        {
            var reg = UniqueIDScriptable.AllUniqueObjects;
            if (reg != null)
                foreach (var kv in reg)
                {
                    var o = kv.Value;
                    if (o == null) continue;
                    scanned++;
                    var ro = Retype(o);
                    if (!ClassCouldHoldAny(ro.GetType()))
                    {
                        skipped++;
                        continue;
                    }

                    CollectNamedMulti(ro, 0);
                }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[NAMEIDX] 单遍建索引异常: " + e.GetType().Name + " " + e.Message);
        }

        var parts = new System.Collections.Generic.List<string>();
        foreach (var t in WantedIndexTypes)
            parts.Add(t + "=" + (NameIndex.TryGetValue(t, out var d) ? d.Count : 0));
        MelonLogger.Msg("[NAMEIDX] 单遍建索引完成: 扫描对象=" + scanned + " 按类跳过=" + skipped
                        + " 耗时=" + (Environment.TickCount - t0) + "ms | " + string.Join(" ", parts));
    }

    private static bool ClassCouldHoldAny(Type cls)
    {
        if (ClassCandidateCache.TryGetValue(cls, out var cached)) return cached;
        var v = false;
        try
        {
            foreach (var kv in WarpperClassGen.MainGen.GetOrGen(cls))
            {
                var ft = kv.Value.fldType;
                if (ft == null) continue;
                var en = ElementTypeNameOf(ft);
                if (IsWantedType(en) || ((ft.IsGenericType || ft.IsArray) && CouldHoldTarget(en)))
                {
                    v = true;
                    break;
                }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        ClassCandidateCache[cls] = v;
        return v;
    }

    private static bool IsWantedType(string en)
    {
        foreach (var t in WantedIndexTypes)
            if (t == en)
                return true;
        return false;
    }

    private static void CollectNamedMulti(object obj, int depth)
    {
        if (obj == null || depth > 2 || NameIndexBudget <= 0) return;
        NameIndexBudget--;
        try
        {
            foreach (var kv in WarpperClassGen.MainGen.GetOrGen(obj.GetType()))
            {
                var ft = kv.Value.fldType;
                if (ft == null) continue;
                var en = ElementTypeNameOf(ft);
                var wanted = NameIndex.TryGetValue(en, out var dict);
                var recurse = !wanted && (ft.IsGenericType || ft.IsArray) && depth < 2 && CouldHoldTarget(en);
                if (!wanted && !recurse) continue;

                object v;
                try
                {
                    v = WarpperClassGen.MainGenTools.CommonGet(obj, kv.Key);
                }
                catch
                {
                    continue;
                }

                if (v == null) continue;
                var n = (int)ElemCount(v);
                if (n <= 0)
                {
                    if (wanted) AddNamedTo(dict, en, v);
                    continue;
                }

                for (var i = 0; i < n && i < 2000; i++)
                {
                    var e = Retype(GetElem(v, i));
                    if (e == null) continue;
                    if (wanted) AddNamedTo(dict, en, e);
                    else CollectNamedMulti(e, depth + 1);
                }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    private static void AddNamedTo(Dictionary<string, object> dict, string typeName, object o)
    {
        try
        {
            var nm = Member(o, "name")?.ToString();
            if (string.IsNullOrEmpty(nm)) return;
            if (dict.ContainsKey(nm))
            {
                NameIndexConflicts[typeName] = NameIndexConflicts.GetValueOrDefault(typeName) + 1;
                return;
            }

            dict[nm] = o;
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    public static object NameIndexFind(string typeName, string name)
    {
        if (!AllIndexesBuilt) EnsureAllNameIndexes();
        if (NameIndex.TryGetValue(typeName, out var d) && d.TryGetValue(name, out var o)) return o;
        if (!WantedContains(typeName))
        {
            EnsureNameIndex(typeName);      // 预设集合外的类型 → 单类型兜底扫描
            if (NameIndex.TryGetValue(typeName, out var d2) && d2.TryGetValue(name, out var o2)) return o2;
        }

        return null;
    }

    private static bool WantedContains(string t)
    {
        foreach (var x in WantedIndexTypes)
            if (x == t)
                return true;
        return false;
    }

    public static void EnsureNameIndex(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || NameIndex.ContainsKey(typeName)) return;
        var dict = new Dictionary<string, object>();
        NameIndex[typeName] = dict;      // 先登记，避免递归重入
        var added = 0;
        var conflict = 0;
        var scanned = 0;
        NameIndexBudget = 300000;
        var t0 = Environment.TickCount;
        try
        {
            var reg = UniqueIDScriptable.AllUniqueObjects;
            if (reg != null)
                foreach (var kv in reg)
                {
                    var o = kv.Value;
                    if (o == null) continue;
                    scanned++;
                    CollectNamed(o, typeName, dict, ref added, ref conflict, 0);
                }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[NAMEIDX] " + typeName + " 扫描异常: " + e.GetType().Name + " " + e.Message);
        }

        NameIndexConflicts[typeName] = conflict;
        MelonLogger.Msg("[NAMEIDX] " + typeName + " 扫描对象=" + scanned + " 建索引=" + added
                        + " 冲突=" + conflict + " 耗时=" + (Environment.TickCount - t0) + "ms"
                        + (added > 0 ? " ✓ 可按名解析" : "（未找到此类型的引用源）"));
    }

    /// <summary>名字索引扫描预算（防止深下探把加载时间拖长）。</summary>
    private static int NameIndexBudget;

    /// <summary>收集对象自身/其字段里类型名为 typeName 的引用，按 `.name` 建索引。depth ≤2 用于下探动作里的音效。</summary>
    private static void CollectNamed(object obj, string typeName, Dictionary<string, object> dict,
        ref int added, ref int conflict, int depth)
    {
        if (obj == null || depth > 2 || NameIndexBudget <= 0) return;
        NameIndexBudget--;
        try
        {
            // ★ 关键：注册表里的对象常被包成基类 `UniqueIDScriptable`，gen 表会因此是空的
            //   （所以第一版扫描 `CardTag`/`EquipmentTag`/`AudioClip` 全是 0 条）。先按真实类名重建代理。
            obj = Retype(obj);
            foreach (var kv in WarpperClassGen.MainGen.GetOrGen(obj.GetType()))
            {
                var ft = kv.Value.fldType;
                if (ft == null) continue;
                var elemName = ElementTypeNameOf(ft);
                var isTarget = elemName == typeName;
                var isObjectArray = ft.IsGenericType || ft.IsArray;
                if (!isTarget && !(isObjectArray && depth < 2 && CouldHoldTarget(elemName)))
                    continue;

                object v;
                try
                {
                    v = WarpperClassGen.MainGenTools.CommonGet(obj, kv.Key);
                }
                catch
                {
                    continue;
                }

                if (v == null) continue;
                var n = (int)ElemCount(v);

                if (n <= 0)
                {
                    // 非容器字段：值本身就是目标类型
                    if (isTarget) AddNamed(v, dict, ref added, ref conflict);
                    continue;
                }

                for (var i = 0; i < n && i < 2000; i++)
                {
                    var e = Retype(GetElem(v, i));
                    if (e == null) continue;
                    if (isTarget) AddNamed(e, dict, ref added, ref conflict);
                    else CollectNamed(e, typeName, dict, ref added, ref conflict, depth + 1);
                }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>
    /// 下探白名单：只进「动作/交互/修正/效果」这类容器类去找目标引用。
    /// 不加限制会把 2858 个对象 × 每个 140 个字段全读一遍（实测 20 秒，不可接受）。
    /// </summary>
    private static bool CouldHoldTarget(string elemName)
    {
        return elemName.EndsWith("Action") || elemName.EndsWith("Actions")
               || elemName.EndsWith("Interaction") || elemName.EndsWith("Modification")
               || elemName.EndsWith("Effect") || elemName.EndsWith("Effects")
               || elemName.Contains("Sound") || elemName.Contains("Sprite") || elemName.Contains("Image");
    }

    private static void AddNamed(object o, Dictionary<string, object> dict, ref int added, ref int conflict)
    {
        try
        {
            var nm = Member(o, "name")?.ToString();
            if (string.IsNullOrEmpty(nm)) return;
            if (dict.ContainsKey(nm))
            {
                conflict++;
                return;
            }

            dict[nm] = o;
            added++;
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    private static string ElementTypeNameOf(Type ft)
    {
        try
        {
            if (ft.IsArray) return ft.GetElementType()?.Name ?? ft.Name;
            if (ft.IsGenericType)
            {
                var ga = ft.GetGenericArguments();
                if (ga.Length == 1) return ga[0].Name;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return ft.Name;
    }

    public static Type FindTypeByName(string name)
    {
        if (TypeByNameCache.TryGetValue(name, out var cached)) return cached;
        Type found = null;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var n = asm.GetName().Name;
                    if (n == null) continue;
                    if (!(n.StartsWith("Assembly-CSharp") || n.StartsWith("Il2Cpp") || n.StartsWith("UnityEngine")))
                        continue;
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name != name) continue;
                        found = t;
                        break;
                    }
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

                if (found != null) break;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        TypeByNameCache[name] = found;
        return found;
    }

    // ───────────── 诊断瘦身：计数器 + 一行汇总（lean/off 下代替逐条明细） ─────────────
    public static int NameIndexHits;
    public static int NameIndexMisses;
    public static int EventCardDumps;
    public static int TraceWrites;

    /// <summary>名字索引命中计数（lean 下只累计，不打逐条）。</summary>
    public static void CountNameHit(bool hit)
    {
        if (hit) NameIndexHits++;
        else NameIndexMisses++;
    }

    /// <summary>`[NAMEIDX-SUM]`：名字索引命中/未命中汇总（一条顶掉成百上千条明细）。</summary>
    public static void LogNameIndexSummary()
    {
        if (!MiniLoader.DiagLean) return;
        try
        {
            MelonLogger.Msg("[NAMEIDX-SUM] 命中=" + NameIndexHits + " 未命中=" + NameIndexMisses
                            + "（lean 不打逐条命中，full 每条都打）");
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>
    /// `[DIAGSUM]` 一行汇总：验收主判据在 lean 下就靠这一行。
    /// 只读计数器，**不遍历任何对象**，零开销。
    /// </summary>
    public static string SummaryLine()
    {
        try
        {
            return "[DIAGSUM] 级别=" + MiniLoader.Diag
                   + " | 名字索引命中=" + NameIndexHits + " 未命中=" + NameIndexMisses
                   + " | 数组下潜样本=" + ArrWarpTrace
                   + " | 解析明细样本=" + ResolveTrace
                   + " | 事件卡转储=" + EventCardDumps
                   + " | warp 痕迹=" + TraceWrites;
        }
        catch
        {
            return "[DIAGSUM] 汇总失败";
        }
    }

    /// <summary>
    /// [lean] 事件卡**一行汇总**：条数 + 产物链完整度（替代 full 下的 JSON 全量转储 + 逐字段明细）。
    /// 例：`[EVENTCARD-SUM] Windy_Event_Gift 选项=3 产物非空=3/3 CardTags=0 AllDrops=3 ✓`
    /// </summary>
    public static void LogEventCardSummary(object cardData, string nm, KVProvider json)
    {
        try
        {
            var acts = WarpperClassGen.MainGenTools.CommonGet(cardData, "DismantleActions");
            var n = (int)ElemCount(acts);
            var ok = 0;
            for (var i = 0; i < n; i++)
            {
                var el = GetElem(acts, i);
                if (el == null) continue;
                var produced = Member(el, "ProducedCards");
                if ((int)ElemCount(produced) <= 0) continue;
                var first = GetElem(produced, 0);
                var dropped = Member(first, "DroppedCards");
                if ((int)ElemCount(dropped) <= 0) continue;
                var dc = GetElem(dropped, 0);
                if (dc != null && Member(dc, "DroppedCard") != null) ok++;
            }

            var tags = (int)ElemCount(Member(cardData, "CardTags"));
            var drops = (int)ElemCount(Member(cardData, "AllDrops"));
            MelonLogger.Msg("[EVENTCARD-SUM] " + nm + " json键=" + json.Count
                            + " 选项=" + n + " 产物链完整=" + ok + "/" + n
                            + " CardTags=" + tags + " AllDrops=" + drops
                            + (n > 0 && ok == n ? " ✓" : ""));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[EVENTCARD-SUM] " + nm + " 汇总失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// [ALLDATA 判据] 游戏主数据表 `GameLoad.Instance.DataBase.AllData` 的条目数。
    /// 用于验证 mod 对象是否真的进了控制台/UI 会遍历的那张表；取不到返回 -1。
    /// </summary>
    public static int AllDataCount()
    {
        try
        {
            var db = GameLoad.Instance?.DataBase;
            if (db == null) return -1;
            var all = db.AllData;
            if (all == null) return -1;
            var t = all.GetType();
            var p = t.GetProperty("Count") ?? t.GetProperty("Length");
            if (p == null) return -1;
            var v = p.GetValue(all);
            return v == null ? -1 : Convert.ToInt32(v);
        }
        catch
        {
            return -1;
        }
    }
    /// <summary>
    /// 按 interop 类型新建一个实例（`il2cpp_object_new` + 包装）—— 给"warp 需要**追加一个对象元素**"用
    /// （GameSourceModify 的 `*WarpData` 元素是完整对象；普通类型不是 ScriptableObject，走不了 CreateScriptableObject 通道）。
    /// </summary>
    public static object NewElementOf(Type t)
    {
        if (t == null) return null;
        try
        {
            var cls = NativeClassOf(t);
            if (cls == IntPtr.Zero) cls = FindClassPtrByClassName(t.Name);   // 兜底：从同类现成实例借类
            if (cls == IntPtr.Zero) return null;
            var ptr = IL2CPP.il2cpp_object_new(cls);
            if (ptr == IntPtr.Zero) return null;
            return Activator.CreateInstance(t, new object[] { ptr });
        }
        catch
        {
            return null;
        }
    }
    /// <summary>
    /// 把一个**引用字段**指向某个现成对象（优先走生成的属性 setter，失败则按字段偏移 + 写屏障）。
    /// 用于"嵌套子对象为 null → 先建出来再递归 warp"（例如新增元素的 `ProducedCards`，它是一个
    /// `CardsDropCollection`，不建出来就永远是空的 —— 空动作的另一半原因）。
    /// </summary>
    public static bool SetObjectField(object host, string fld, object value)
    {
        try
        {
            if (host == null || value == null) return false;
            var p = host.GetType().GetProperty(fld);
            if (p != null && p.CanWrite)
            {
                p.SetValue(host, value);
                RefFieldSet++;
                return true;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        try
        {
            if (host is Il2CppObjectBase hb && value is Il2CppObjectBase vb)
            {
                var gen = WarpperClassGen.MainGen.GetOrGen(host.GetType());
                if (gen.TryGetValue(fld, out var tuple))
                {
                    var hp = hb.Pointer;
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_wbarrier_set_field(hp, hp + tuple.fOffset, vb.Pointer);
                    RefFieldSet++;
                    return true;
                }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        if (RefFieldFailSamples.Count < 10)
            RefFieldFailSamples.Add(host.GetType().Name + "." + fld);
        RefFieldFail++;
        return false;
    }

    public static int RefFieldSet, RefFieldFail;
    public static readonly List<string> RefFieldFailSamples = new();
    // ═══════════ 托管成员访问：属性优先 → public 字段兜底（★ 零偏移、零裸内存） ═══════════
    //  ★ [2026-10-03 第 10 轮 · 真根因] `prop=null` ×95,180 不是"interop 没生成属性"，而是
    //    **Il2CppInterop 对 il2cpp 值类型有两种生成形态**（用 System.Reflection.Metadata 直接读
    //    设备上的 Il2CppAssemblies/Assembly-CSharp.dll 实证）：
    //      · 含引用的值类型（`LocalizedString`/`DurabilityConditions`/`CardInteractionTrigger`）
    //        → 生成为 `Il2CppSystem.ValueType` 派生的**类 + 属性**（旧代码这条路是对的，成功 429,200）；
    //      · **blittable 的值类型**（`DurabilitiesConditions`/`DurabilityWeightValue`/`EncounterVariable`/
    //        `EnemySkillModifier`/`LightSourceSettings`/`Vector2`/`Color`…）
    //        → 生成为 `ExplicitLayout` 的 **C# struct + public 字段**，**没有属性**。
    //    于是 `host.GetType().GetProperty(fld)` 恒为 null → 刷屏 + 这一整类字段**从来没写进去**。
    //    通用访问因此必须"属性优先、字段兜底"；两条都只用托管反射，绝不碰非托管内存。
    public static int StructProxyWrites, StructProxyFails;
    /// <summary>走"public 字段兜底"写回的次数（= 本轮新修好的那一类）。</summary>
    public static int StructMemberFieldWrites;
    /// <summary>`CommonSetFld` 里"直接把 data 写进目标成员"的次数（原"源→目标"错位分支的修复量）。</summary>
    public static int MemberDirectWrites;
    /// <summary>结构写回后**读回与写入值不一致**的次数（0 = 值确实进了真对象，不是只改了托管副本）。</summary>
    public static int StructReadBackFails;
    /// <summary>实际做过的"读回校验"次数（证明上面那个 0 不是"没校验"）。</summary>
    public static int StructReadBackChecks;
    private static int MemberAccessWarned;
    private static int ScalarConvertWarned;

    /// <summary>
    /// 读一个**托管可见成员**：属性优先，其次 public 实例字段。
    /// `viaField=true` 表示命中的是字段（blittable 值类型那条路）。
    /// </summary>
    public static bool TryReadMember(object host, string name, out object value, out Type memberType, out bool canWrite,
        out bool viaField)
    {
        value = null;
        memberType = null;
        canWrite = false;
        viaField = false;
        if (host == null || string.IsNullOrEmpty(name)) return false;
        try
        {
            var mi = FindMember(host.GetType(), name);
            if (mi is System.Reflection.PropertyInfo p)
            {
                if (!p.CanRead) return false;
                memberType = p.PropertyType;
                canWrite = p.CanWrite;
                value = p.GetValue(host);
                return true;
            }

            if (mi is System.Reflection.FieldInfo f)
            {
                memberType = f.FieldType;
                canWrite = !f.IsInitOnly && !f.IsLiteral;
                viaField = true;
                value = f.GetValue(host);
                return true;
            }
        }
        catch (Exception e)
        {
            if (MemberAccessWarned < 8)
            {
                MemberAccessWarned++;
                MelonLogger.Warning("[INL] 读托管成员失败: " + host.GetType().Name + "." + name
                                    + " : " + e.GetType().Name + " " + e.Message);
            }
        }

        return false;
    }

    /// <summary>
    /// 成员查找（**带缓存**，避免 `Type.GetProperty(name, flags)` 的 AmbiguousMatchException 与逐次反射开销）：
    /// 属性优先（跳过索引器），其次 public 实例字段。
    /// </summary>
    private static System.Reflection.MemberInfo FindMember(Type t, string name)
    {
        if (t == null) return null;
        var key = (t.AssemblyQualifiedName ?? t.Name) + "|" + name;
        if (MemberCache.TryGetValue(key, out var hit)) return hit;
        if (MemberMisses.Contains(key)) return null;
        try
        {
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.Name != name || p.GetIndexParameters().Length != 0) continue;
                MemberCache[key] = p;
                return p;
            }

            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name != name) continue;
                MemberCache[key] = f;
                return f;
            }
        }
        catch (Exception e)
        {
            if (MemberAccessWarned < 8)
            {
                MemberAccessWarned++;
                MelonLogger.Warning("[INL] 成员查找失败: " + t.Name + "." + name
                                    + " : " + e.GetType().Name + " " + e.Message);
            }
        }

        MemberMisses.Add(key);
        return null;
    }

    private static readonly Dictionary<string, System.Reflection.MemberInfo> MemberCache = new();
    private static readonly HashSet<string> MemberMisses = new();

    /// <summary>
    /// 写一个**托管可见成员**：属性优先，其次 public 实例字段。
    /// 类型不匹配或只读 → 返回 false 且**什么都不写**（绝不做隐式强转，避免把错类型塞进结构）。
    /// </summary>
    public static bool TryWriteMember(object host, string name, object value)
    {
        if (host == null) return false;
        try
        {
            var mi = FindMember(host.GetType(), name);
            if (mi is System.Reflection.PropertyInfo p)
            {
                if (!p.CanWrite) return false;
                if (value != null && !p.PropertyType.IsInstanceOfType(value)) return false;
                p.SetValue(host, value);
                return true;
            }

            if (mi is System.Reflection.FieldInfo f)
            {
                if (f.IsInitOnly || f.IsLiteral) return false;
                if (value != null && !f.FieldType.IsInstanceOfType(value)) return false;
                f.SetValue(host, value);
                return true;
            }
        }
        catch (Exception e)
        {
            if (MemberAccessWarned < 8)
            {
                MemberAccessWarned++;
                MelonLogger.Warning("[INL] 写托管成员失败: " + host.GetType().Name + "." + name
                                    + " : " + e.GetType().Name + " " + e.Message);
            }
        }

        return false;
    }

    /// <summary>JSON → 托管标量（bool/整型族/浮点/字符串/枚举）。只做**精确**转换，失败即 false（不猜）。</summary>
    public static bool TryConvertScalar(Type t, KVProvider v, out object val)
    {
        val = null;
        if (t == null || v == null) return false;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (t == typeof(string))
            {
                val = v.IsString ? v.String : RawScalarText(v)?.Trim('"');
                return true;
            }

            if (t == typeof(bool))
            {
                val = v.IsBoolean ? v.Bool : bool.Parse(RawScalarText(v));
                return true;
            }

            if (t == typeof(float)) { val = v.IsInt ? v.Int : float.Parse(RawScalarText(v), inv); return true; }
            if (t == typeof(double)) { val = v.IsInt ? (double)v.Int : double.Parse(RawScalarText(v), inv); return true; }
            if (t == typeof(int)) { val = v.IsInt ? v.Int : int.Parse(RawScalarText(v), inv); return true; }
            if (t == typeof(long)) { val = v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv); return true; }
            // 整型族统一"先取 long 再显式窄化"，避免 C# 目标类型条件表达式把 int 直接塞进 uint/ulong
            if (t == typeof(uint)) { val = (uint)(v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv)); return true; }
            if (t == typeof(ulong)) { val = (ulong)(v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv)); return true; }
            if (t == typeof(short)) { val = (short)(v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv)); return true; }
            if (t == typeof(ushort)) { val = (ushort)(v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv)); return true; }
            if (t == typeof(byte)) { val = (byte)(v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv)); return true; }
            if (t == typeof(sbyte)) { val = (sbyte)(v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv)); return true; }
            if (t.IsEnum)
            {
                val = Enum.ToObject(t, v.IsInt ? (long)v.Int : long.Parse(RawScalarText(v), inv));
                return true;
            }
        }
        catch (Exception e)
        {
            if (ScalarConvertWarned < 6)
            {
                ScalarConvertWarned++;
                MelonLogger.Warning("[INL] 标量转换失败: 目标类型=" + t.Name
                                    + " 形态=" + v.GetType().Name + " IsInt=" + v.IsInt + " IsString=" + v.IsString
                                    + " 原文=" + v.ToJson() + " : " + e.GetType().Name + " " + e.Message);
            }
        }

        return false;
    }

    /// <summary>
    /// 取一个 JSON 标量的**文本形态**。必须同时覆盖两种 KVProvider 实现（真机踩过）：
    ///   · `JsonKVProvider`（LitJson）  → `String` 就是数字/布尔的文本；
    ///   · `MapperItem`（modArch 二进制）→ **非字符串时 `String` 恒为空串**（`ObjDouble`/`ObjInt`…），
    ///     文本只在 `ToJson()` 里 —— 这正是 56 万次 `Vector2.x/y` 标量写不进去的原因。
    /// </summary>
    public static string RawScalarText(KVProvider v)
    {
        if (v == null) return null;
        try
        {
            if (v.IsString) return v.String;
            var s = v.String;
            if (!string.IsNullOrEmpty(s)) return s.Trim();
            s = v.ToJson();
            if (!string.IsNullOrEmpty(s)) return s.Trim();
            return v.ToString()?.Trim();
        }
        catch (Exception e)
        {
            if (ScalarConvertWarned < 6)
            {
                ScalarConvertWarned++;
                MelonLogger.Warning("[INL] 标量取文本失败: " + e.GetType().Name + " " + e.Message);
            }

            return null;
        }
    }

    /// <summary>
    /// 浅层逐成员比较（深度 ≤2）—— 只用于"整块结构写回是否真的生效"的客观判据。
    /// 值类型字段写不进去时，读回值会与写入值不一致（值语义副本），这是可判定的。
    /// </summary>
    public static bool SameMemberValues(object a, object b, int depth = 0)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        var ta = a.GetType();
        if (ta != b.GetType()) return false;
        if (ta.IsPrimitive || ta.IsEnum || a is string) return a.Equals(b);
        if (depth >= 2) return true;
        try
        {
            foreach (var f in ta.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try
                {
                    if (!SameMemberValues(f.GetValue(a), f.GetValue(b), depth + 1)) return false;
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }

            foreach (var p in ta.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                try
                {
                    if (!SameMemberValues(p.GetValue(a), p.GetValue(b), depth + 1)) return false;
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return true;
    }

    /// <summary>
    /// 值类型（内联结构）字段的**安全写入**：取托管副本（属性或 public 字段）→ 在副本上递归 warp
    /// （`*WarpData` 引用会因此解析进 `TriggerCards`/`TriggerTags` 等子字段）→ **整块写回**字段。
    /// 全程只用托管成员，**不使用字段偏移、不做 memcpy、不写裸内存**。
    /// 用途：mod 卡的 `CardInteractions[i].CompatibleCards` 若是内联结构，主 warp 会跳过它 →
    /// `TriggerCards` 永远为空 → 拖拽交互永不匹配（真机 37/37 全空就是这个）。
    /// </summary>
    public static bool TrySetStructViaProxy(object host, string fld, KVProvider v)
    {
        try
        {
            if (host == null || v == null) return false;
            if (!TryReadMember(host, fld, out var proxy, out var memberType, out var canWrite, out var viaField)
                || !canWrite)
            {
                // ★ [2026-10-03 第 10 轮] 这里原来是**逐条 MelonLogger.Warning**：真机 95,180 行 / 30MB 日志，
                //   把日志撑爆且掩盖了真根因。现在统一走"**(类型.字段|原因) 去重 + 计数**"
                //   （= 用户要求の条件筛选：成功只计数、失败去重计数；**不是 cap/截断**）。
                StructProxyFails++;
                NoteInlineIssue(host.GetType().Name + "." + fld + "|结构字段无可用成员",
                    "属性与 public 字段都取不到（或只读）→ 该内联结构字段未被赋值");
                return false;
            }

            if (proxy == null)
            {
                // 结构未初始化 → 先造一个同类型实例挂上（仍是托管赋值：值类型走 Activator，引用类型走 il2cpp_object_new）
                var made = memberType != null && memberType.IsValueType
                    ? Activator.CreateInstance(memberType)
                    : NewElementOf(memberType);
                if (made == null)
                {
                    StructProxyFails++;
                    NoteInlineIssue(host.GetType().Name + "." + fld + "|结构实例创建失败",
                        "类型=" + (memberType?.Name ?? "?"));
                    return false;
                }

                if (!TryWriteMember(host, fld, made) ||
                    !TryReadMember(host, fld, out proxy, out _, out _, out _) || proxy == null)
                {
                    StructProxyFails++;
                    NoteInlineIssue(host.GetType().Name + "." + fld + "|结构实例挂载失败",
                        "类型=" + (memberType?.Name ?? "?"));
                    return false;
                }
            }

            // 在托管副本上写子字段（含嵌套 `*WarpData` 解引用）
            WarpperClassGen.WarpFunc.JsonCommonWarpper(proxy, v);

            // 整块结构写回（属性 setter 内部就是 il2cpp 官方 field_set_value；字段则直接写托管结构）
            if (!TryWriteMember(host, fld, proxy))
            {
                StructProxyFails++;
                NoteInlineIssue(host.GetType().Name + "." + fld + "|结构写回被拒",
                    "属性/字段 setter 拒绝该值（类型不匹配或只读）");
                return false;
            }

            StructProxyWrites++;   // [条件筛选] 成功只计数，不逐条打（真机实测逐条打会刷到 11MB）
            if (viaField) StructMemberFieldWrites++;

            // ★ 读回验证（只对**真 il2cpp 对象上的值类型字段**做）：证明值确实进了真对象，
            //   而不是只改了托管副本（后者正是"日志说成功、游戏里没变"的经典假象）。
            if (memberType is { IsValueType: true } && host is Il2CppObjectBase &&
                TryReadMember(host, fld, out var back, out _, out _, out _))
            {
                StructReadBackChecks++;
                if (!SameMemberValues(back, proxy))
                {
                    StructReadBackFails++;
                    NoteInlineIssue(host.GetType().Name + "." + fld + "|读回未生效", "写入后读回与写入值不一致（值语义副本）");
                }
            }

            return true;
        }
        catch (Exception e)
        {
            StructProxyFails++;
            NoteInlineIssue((host?.GetType().Name ?? "?") + "." + fld + "|结构写回异常",
                e.GetType().Name + " " + e.Message);
            return false;
        }
    }

    /// <summary>全桶按名查找（`TryResolveRefByName` 的第三级兜底）：`Sprite`/`CardTag` 这类
    /// 按名字引用的对象，有时类型名与字段泛型参数对不上，就直接在所有索引桶里找。</summary>
    public static object NameIndexFindAny(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in new List<string>(NameIndex.Keys))
            {
                var o = NameIndexFind(t, name);
                if (o != null) return o;
            }

            foreach (var t in WantedIndexTypes)
            {
                var o = NameIndexFind(t, name);
                if (o != null) return o;
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return null;
    }
    // ═══════════ 引用解析未命中清单（Lead 指定：失败要打 GUID 原文 + 类型 + 来源） ═══════════
    private static readonly HashSet<string> ResolveMissSeen = new();
    public static readonly List<string> ResolveMisses = new();

    /// <summary>记录一次"引用没解析出来"（完整清单，无上限）。</summary>
    public static void AddResolveMiss(string typeName, string id, string form = "?", string field = "?",
        string indexes = "?")
    {
        try
        {
            // [形态分派审计] 措辞修正：不再把所有值都印成"GUID="（那会把名字也印成 GUID，误导定位）。
            // 现在打印 形态 + 值 + 字段名 + 目标类型 + 查过的索引规模 → "数据缺失"与"索引不对"一眼可分。
            var key = typeName + "|" + form + "|" + id;
            if (!ResolveMissSeen.Add(key)) return;
            ResolveMisses.Add("[RESOLVE] 未解析: 形态=" + form + " 值=" + id + " 字段=" + field
                              + " 目标类型=" + typeName + " 查过=" + indexes);
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    // ═══════════ 官方字段 API 回退（结构字段没有生成属性时）—— 零偏移、零裸内存 ═══════════
    /// <summary>
    /// 目标字段在 interop 里**没有生成属性**时的通用回退：用运行时官方字段 API + boxed 结构拷贝
    /// （`il2cpp_field_get_value` 源 → boxed；`il2cpp_field_set_value` boxed → 目标）。
    /// 真机证据：`DurabilitiesConditions.SpecialNRange` 等一整类结构字段 `prop=null`（interop 没生成属性），
    /// 31,028 次刷屏即来自这里 —— 不用生成属性也能按**真实 il2cpp 字段**读写。
    /// </summary>
    public static bool CopyStructFieldViaFieldApi(object target, object source, string fld)
    {
        try
        {
            if (target is not Il2CppObjectBase tb || source is not Il2CppObjectBase sb) return false;
            var gt = WarpperClassGen.MainGen.GetOrGen(target.GetType());
            var gs = WarpperClassGen.MainGen.GetOrGen(source.GetType());
            if (!gt.TryGetValue(fld, out var tt) || !gs.TryGetValue(fld, out var ts)) return false;
            if (tt.fPtr == IntPtr.Zero || ts.fPtr == IntPtr.Zero) return false;

            var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_il2cpp_type(
                Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_type(tt.fPtr));
            if (cls == IntPtr.Zero) return false;
            var box = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(cls);   // boxed 结构
            if (box == IntPtr.Zero) return false;
            var unbox = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(box);
            if (unbox == IntPtr.Zero) return false;

            unsafe
            {
                Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_value(sb.Pointer, ts.fPtr, (void*)unbox);
                Il2CppInterop.Runtime.IL2CPP.il2cpp_field_set_value(tb.Pointer, tt.fPtr, (void*)unbox);
            }
            StructProxyWrites++;
            FieldApiCopies++;
            return true;
        }
        catch (Exception e)
        {
            NoteInlineIssue((source?.GetType().Name ?? "?") + "." + fld + "|字段API拷贝异常",
                e.GetType().Name + " " + e.Message);
            return false;
        }
    }

    public static int FieldApiCopies;
    // ═══════════ 内联结构写回：条件筛选 + (类型.字段) 去重计数（绝不逐实例刷屏，也绝不 cap） ═══════════
    public static readonly Dictionary<string, int> InlineIssues = new();
    private static readonly HashSet<string> InlineIssueSeen = new();

    /// <summary>登记一次"值类型/内联结构写回"的问题：按 `类型.字段|原因` 去重 + 计数（成功只计数，不逐条打）。</summary>
    public static void NoteInlineIssue(string key, string detail)
    {
        try
        {
            if (InlineIssues.TryGetValue(key, out var n)) InlineIssues[key] = n + 1;
            else InlineIssues[key] = 1;
            if (InlineIssueSeen.Add(key))
                MelonLogger.Warning("[INL] 首次出现: " + key + "（" + detail + "）— 之后只计数，不刷屏");
        }
        catch (Exception e)
        {
            // [掩盖审计] 空 catch 归零：登记失败也要有上下文（绝不再静默）
            MelonLogger.Warning("[INL] 问题登记失败: " + key + " : " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// `[INL] 内联结构写回汇总: 成功=N 失败=M` + 每条唯一问题 `×次数`（去重后完整打印，无上限）。
    /// —— 用户要求：日志**按条件筛**（成功只打计数、失败去重+计数），不是"按数量 cap"。
    /// </summary>
    public static void DumpInlineIssues()
    {
        try
        {
            MelonLogger.Msg("[INL] 内联结构写回汇总: 成功=" + StructProxyWrites + " 失败=" + StructProxyFails
                            + " 唯一问题=" + InlineIssues.Count
                            + " | 成员路径: public字段兜底=" + StructMemberFieldWrites
                            + " 直接写回=" + MemberDirectWrites
                            + " 官方字段API=" + FieldApiCopies
                            + " 读回校验=" + StructReadBackChecks + " 读回不一致=" + StructReadBackFails);
            foreach (var kv in InlineIssues)
                MelonLogger.Warning("[INL]   " + kv.Key + " ×" + kv.Value);
        }
        catch (Exception __e)
        {
            MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message);
        }
    }
    // ═══════════ ★ 按 JSON 形态分派引用定位（禁止"先 GUID 后名字"互相兜底） ═══════════
    /// <summary>值是不是 32 位十六进制 GUID（= 作者 JSON 的 GUID 形态）。</summary>
    public static bool LooksLikeGuid(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    /// <summary>某类型的名字索引规模（失败时打印，用来区分"数据缺失"与"我们的索引不对"）。-1 = 没有该桶。</summary>
    public static int NameIndexCount(string typeName)
    {
        try
        {
            if (!AllIndexesBuilt) EnsureAllNameIndexes();
            return NameIndex.TryGetValue(typeName, out var d) ? d.Count : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// **按 JSON 形态分派**：32 位十六进制 → 只查 GUID 索引（mod 字典 → 游戏注册表）；
    /// 其它形态 → 只查**该字段声明类型**的名字索引。两者**绝不互相兜底**
    /// （审计结论：盲兜底会把"形态/类型不匹配"掩盖成"偶尔成功"）。
    /// </summary>
    public static bool ResolveByJsonForm<T>(string value, string field, out T item)
        where T : Il2CppObjectBase
    {
        item = null;
        if (string.IsNullOrEmpty(value)) return false;
        if (LooksLikeGuid(value))
        {
            if (WarpperClassGen.MainGenTools.TryResolveRef<T>(value, out item)) return true;
            AddResolveMiss(typeof(T).Name, value, "GUID", field,
                "AllGUIDDict 类桶=" + MiniLoader.AllItemDictionary.Count
                + " / AllUniqueObjects=" + (UniqueIDScriptable.AllUniqueObjects?.Count ?? -1));
            return false;
        }

        if (WarpperClassGen.MainGenTools.TryResolveRefByName<T>(value, out item)) return true;
        AddResolveMiss(typeof(T).Name, value, "名字", field,
            "名字索引[" + typeof(T).Name + "]=" + NameIndexCount(typeof(T).Name) + " 项");
        return false;
    }
    // ═══════════ 精灵卡（mod 自己的卡）的交互 dump —— 用户级 bug 定案用（不受 40 条上限限制） ═══════════
    /// <summary>
    /// `[MODCARD] Windy.CardInteractions: 共 N 项` + 逐项 `ActionName / CompatibleCards.TriggerCards / TriggerTags /
    /// ReceivingCardChanges.TransformInto` + 空引用计数 → 一眼定案"引用有没有解出来"。
    /// 只 dump **mod 自己创建的卡**（默认名字含 Windy / 精灵，或 CardInteractions 非空的前 2 张）。
    /// </summary>
    public static void DumpModCardInteractions()
    {
        try
        {
            if (!MiniLoader.DiagLean) return;
            if (!MiniLoader.AllItemDictionary.TryGetValue(typeof(CardData), out var dict) || dict == null) return;

            var dumped = 0;
            foreach (var kv in dict)
            {
                if (kv.Value is not CardData cd || cd == null) continue;
                var nm = NameOf(cd) ?? kv.Key;
                var ci = Member(cd, "CardInteractions");
                var n = (int)ElemCount(ci);

                // [2026-10-03 通用化·修正] **选取规则 = 所有含非空 CardInteractions 的 mod 卡，不设数量上限**。
                // （上一版用"前 N 张"当筛选 → 把排在后面的卡整个漏掉 ✗；数量上限只用于**日志长度保护** ✓，
                if (n <= 0) continue;
                dumped++;
                {
                    continue;
                }
                var emptyCompat = 0;
                var emptyAction = 0;
                var keywordHit = 0;
                MelonLogger.Msg("[MODCARD] " + nm + ".CardInteractions: 共 " + n + " 项"
                                + "（卡 GUID=" + (Member(cd, "UniqueID")?.ToString() ?? "?") + "）");
                for (var i = 0; i < n; i++)
                {
                    var e = Retype(GetElem(ci, i));
                    if (e == null) continue;
                    var an = ReadName(e);
                    var cc = Member(e, "CompatibleCards");
                    var trg = cc == null ? null : Member(cc, "TriggerCards");
                    var tcnt = (int)ElemCount(trg);
                    var tnames = new List<string>();
                    for (var k = 0; k < tcnt && k < 3; k++) tnames.Add(ElemName(GetElem(trg, k)));
                    var tg = cc == null ? null : Member(cc, "TriggerTags");
                    var tgcnt = (int)ElemCount(tg);
                    var tgnames = new List<string>();
                    for (var k = 0; k < tgcnt && k < 3; k++) tgnames.Add(ElemName(GetElem(tg, k)));

                    // 结果字段（"点了会不会有结果"）
                    var rcc = Member(e, "ReceivingCardChanges");
                    var rn = (int)ElemCount(rcc);
                    var ti = "";
                    for (var k = 0; k < rn && k < 2; k++)
                    {
                        var r0 = GetElem(rcc, k);
                        var tr = r0 == null ? null : Member(r0, "TransformInto");
                        var tid = r0 == null ? null : Member(r0, "TransformIntoID");
                        var one = tr == null ? (tid == null ? "-" : DescribeValue(tid)) : ElemName(tr);
                        ti += (k > 0 ? "|" : "") + one;
                    }

                    var prod = Member(e, "ProducedCards");
                    var prodN = (int)ElemCount(prod);
                    var drops = Member(e, "DroppedCards");
                    var dropsN = (int)ElemCount(drops);
                    var gcc = Member(e, "GivenCardChanges");
                    var gccN = (int)ElemCount(gcc);

                    // ★ [2026-10-03] "产物"真正落在这里：`GivenCardChanges.TransformInto`（给的卡变成什么）。
                    //   离线核对 `[3] 缠细线`：`GivenCardChanges.TransformIntoWarpData = "f6e8281f…"` ✓
                    //   （`ReceivingCardChanges` 是"被接收方"的变化 ✗，之前打错了 → 看起来"产物为空" ✗）。
                    var gccTi = gcc == null ? null : Member(gcc, "TransformInto");
                    var gccTiId = gcc == null ? null : Member(gcc, "TransformIntoID");
                    var gccTiStr = gccTi != null ? ElemName(gccTi) : (gccTiId != null ? DescribeValue(gccTiId) : "—");
                    var rccTi = "";
                    for (var kk = 0; kk < (int)ElemCount(rcc) && kk < 2; kk++)
                    {
                        var r0b = GetElem(rcc, kk);
                        var trb = r0b == null ? null : Member(r0b, "TransformInto");
                        var tidb = r0b == null ? null : Member(r0b, "TransformIntoID");
                        rccTi += (kk > 0 ? "|" : "") + (trb != null ? ElemName(trb) : (tidb != null ? DescribeValue(tidb) : "—"));
                    }

                    var statMods = Member(e, "StatModifications");
                    var statModsN = (int)ElemCount(statMods);
                    var reqBoard = Member(e, "RequiredCardsOnBoard");
                    var reqBoardN = (int)ElemCount(reqBoard);
                    var reqDur = Member(e, "RequiredGivenDurabilities");
                    var reqDurN = (int)ElemCount(reqDur);
                    var wbw = Member(e, "WorksBothWays");
                    var carry = Member(e, "CarryOverGivenCard");

                    // ① 兼容条件全空 = "跟任何卡都匹配"（必乱弹 / 也可能就是卡死源）
                    var compatEmpty = tcnt == 0 && tgcnt == 0;
                    if (compatEmpty) emptyCompat++;

                    // ② 空动作 = 没有任何结果字段 → 渲染了选项但点了没结果（很可能卡在拖拽态）
                    var hasTransform = (gccTiStr != "—" && !string.IsNullOrEmpty(gccTiStr)) || (rccTi.Length > 0 && rccTi != "—");
                    var actionEmpty = prodN == 0 && !hasTransform && dropsN == 0 && statModsN == 0;
                    if (actionEmpty) emptyAction++;

                    // ③ 诊断关键字（喂/食/Feed/Eat）——纯标记，用于定位"为什么会弹出喂食"
                    var kw = an != null && (an.Contains("喂") || an.Contains("食")
                                            || an.IndexOf("feed", StringComparison.OrdinalIgnoreCase) >= 0
                                            || an.IndexOf("eat", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (kw) keywordHit++;

                    // ★ 逐字段对账：把该条的**作者原始 JSON** 与加载后对象比对，只报差异（无上限）
                    try
                    {
                        var guidOfCard = Member(cd, "UniqueID")?.ToString();
                        if (guidOfCard != null && ModCardJsonSource.TryGetValue(guidOfCard, out var src) &&
                            src != null && src.ContainsKey("CardInteractions") && src["CardInteractions"].IsArray &&
                            i < src["CardInteractions"].Count)
                            DiffJsonVsObject(e, src["CardInteractions"][i],
                                             nm + ".CardInteractions[" + i + "] \"" + an + "\"");
                    }
                    catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

                    MelonLogger.Msg("[MODCARD]   [" + i + "] \"" + an + "\""
                                    + (kw ? " ★关键字" : "")
                                    + " TriggerCards(项)=" + tcnt + "[" + string.Join("|", tnames) + "]"
                                    + " TriggerTags(项)=" + tgcnt + "[" + string.Join("|", tgnames) + "]"
                                    + " RequiredCardsOnBoard(项)=" + reqBoardN
                                    + " RequiredGivenDurabilities(项)=" + reqDurN
                                    + " WorksBothWays=" + (wbw?.ToString() ?? "-")
                                    + " CarryOverGivenCard=" + (carry?.ToString() ?? "-")
                                    + " ProducedCards(项)=" + prodN
                                    + " DroppedCards(项)=" + dropsN
                                    + " StatModifications(项)=" + statModsN
                                    + " ★GivenCardChanges.TransformInto=" + gccTiStr
                                    + " ReceivingCardChanges.TransformInto=" + rccTi
                                    + (compatEmpty ? " ⚠兼容条件全空(任何卡都匹配)" : "")
                                    + (actionEmpty ? " ⚠空动作(无任何结果字段)" : ""));
                }

                MelonLogger.Msg("[MODCARD] " + nm + " 判定: 共 " + n + " 项；兼容条件全空的项=" + emptyCompat
                                + "；空动作条目=" + emptyAction + "；关键字(喂/食/feed/eat)命中=" + keywordHit);

                // 字段类型信息（判断 CompatibleCards 是不是"内联值类型" —— 若是，warp 会跳过它，引用永远解不出来）
                // 字段类型信息：必须用**元素**的 gen 表（`CompatibleCards` 是 CardInteraction 的字段，
                // 不是 CardData 的 → 之前查 CardData 得到 "?"）
                var first = Retype(GetElem(ci, 0));
                var eg = first != null ? WarpperClassGen.MainGen.GetOrGen(first.GetType()) : null;
                MelonLogger.Msg("[MODCARD] " + nm + " 元素类型=" + (first == null ? "?" : first.GetType().Name));
                if (eg != null)
                {
                    foreach (var fn in new[] { "CompatibleCards", "TriggerCards", "TriggerTags", "ReceivingCardChanges", "GivenCardChanges", "ProducedCards", "StatModifications" })
                    {
                        if (!eg.TryGetValue(fn, out var tu)) continue;
                        var cls = IntPtr.Zero;
                        try
                        {
                            cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_il2cpp_type(
                                Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_type(tu.fPtr));
                        }
                        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

                        MelonLogger.Msg("[MODCARD]   字段 " + fn + ": 声明类型=" + (tu.fldType?.Name ?? "?")
                                        + " 真实il2cpp类=" + (cls == IntPtr.Zero ? "?" : AsciiClassNameOf(cls))
                                        + " valueType=" + tu.isValueType + " 偏移=0x" + tu.fOffset.ToString("X")
                                        + " 属性可写=" + (first.GetType().GetProperty(fn)?.CanWrite ?? false));
                    }
                }
            }

            // ★ 通用断言（不依赖任何 mod 名/卡名）：所有 mod 卡里"引用型字段全空"的项必须为 0
            var totalItems = 0;
            var totalEmpty = 0;
            try
            {
                foreach (var kv2 in dict)
                {
                    if (kv2.Value is not CardData cd2 || cd2 == null) continue;
                    var ci2 = Member(cd2, "CardInteractions");
                    var n2 = (int)ElemCount(ci2);
                    for (var i2 = 0; i2 < n2; i2++)
                    {
                        var e2 = Retype(GetElem(ci2, i2));
                        if (e2 == null) continue;
                        totalItems++;
                        var cc2 = Member(e2, "CompatibleCards");
                        var trg2 = cc2 == null ? null : Member(cc2, "TriggerCards");
                        var tg2 = cc2 == null ? null : Member(cc2, "TriggerTags");
                        // JSON 里有 *WarpData 才算"应当非空"；这里用"该字段在 JSON 里出现过"无法回溯，
                        // 故按通用口径统计：TriggerCards 与 TriggerTags **同时**为空的项
                        if ((int)ElemCount(trg2) == 0 && (int)ElemCount(tg2) == 0) totalEmpty++;
                    }
                }

                                MelonLogger.Msg("[MODCARD] 通用汇总: 含 CardInteractions 的 mod 卡=" + dumped
                                + "；交互项=" + totalItems + " 其中 TriggerCards/TriggerTags 全空=" + totalEmpty
                                + (totalEmpty == 0 ? " ✓ 所有引用型判定字段都已填" : " ⚠ 有全空项（可能是引用未解析）"));
            }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

            if (dumped == 0) MelonLogger.Msg("[MODCARD] 本包没有带 CardInteractions 的 mod 卡（无需判定）");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[MODCARD] dump 失败: " + e.GetType().Name + " " + e.Message);
        }
    }
    // ═══════════ "能不能拖上去"的判定字段转储（用户级 bug 判据） ═══════════
    public static int TriggerDumpLogged;

    /// <summary>
    /// `[GSM] 新增动作字段: <宿主>.<字段>[i] ActionName="…" CompatibleCards.TriggerCards=[…] TriggerTags=[…]
    ///  RequiredCardsOnBoard=[…] ReceivingCardChanges.TransformInto=[…] 引用解析=OK/失败`
    /// —— Lead 指定：一眼看出"判定字段齐不齐、引用通不通"。
    /// </summary>
    public static void DumpTriggerFields(object element, KVProvider json, string label)
    {
        try
        {
            if (element == null) return;
            if (TriggerDumpLogged > 40) return;
            TriggerDumpLogged++;

            var name = ReadName(element);
            var sb = new System.Text.StringBuilder();
            sb.Append("[GSM] 新增动作字段: ").Append(label)
              .Append(" ActionName=\"").Append(name).Append('"');

            // 关键判定字段（有就打印，没有就标 —）
            foreach (var f in new[]
                     {
                         "CompatibleCards", "RequiredCardsOnBoard", "RequiredTagsOnBoard", "ProducedCards",
                         "ReceivingCardChanges", "InstantStatModifications", "ActionTags", "GivenCardChanges"
                     })
            {
                var v = Member(element, f);
                if (v == null)
                {
                    sb.Append(' ').Append(f).Append("=—");
                    continue;
                }

                var n = (int)ElemCount(v);
                if (n > 0 || IsIl2CppArrayType(v.GetType()))
                {
                    var names = new List<string>();
                    for (var i = 0; i < n && i < 4; i++) names.Add(ElemName(GetElem(v, i)));
                    sb.Append(' ').Append(f).Append("=[").Append(string.Join("|", names)).Append(']');
                }
                else
                {
                    sb.Append(' ').Append(f).Append('=').Append(DescribeValue(v));
                }
            }

            // CompatibleCards 内部（拖拽判定真正看的两个列表）
            var cc = Member(element, "CompatibleCards");
            if (cc != null)
            {
                foreach (var sub in new[] { "TriggerCards", "TriggerTags" })
                {
                    var sv = Member(cc, sub);
                    var sn = (int)ElemCount(sv);
                    var names = new List<string>();
                    for (var i = 0; i < sn && i < 4; i++) names.Add(ElemName(GetElem(sv, i)));
                    sb.Append(" CompatibleCards.").Append(sub).Append("=[").Append(string.Join("|", names)).Append(']');
                }
            }

            // ReceivingCardChanges.TransformInto（"缠成细线/变成粘土"这类产物就落在这里）
            var rcc = Member(element, "ReceivingCardChanges");
            var rn = (int)ElemCount(rcc);
            for (var i = 0; i < rn && i < 2; i++)
            {
                var e = GetElem(rcc, i);
                var ti = Member(e, "TransformInto");
                var tid = Member(e, "TransformIntoID");
                sb.Append(" ReceivingCardChanges[").Append(i).Append("].TransformInto=")
                  .Append(ti == null ? DescribeValue(tid) : (ElemName(ti) + "/" + DescribeValue(tid)));
            }

            // 引用解析判定：JSON 里凡有 `<X>WarpData`，就看对应字段是否真的非空
            var verdict = CheckRefsResolved(element, json, out var detail);
            sb.Append(" 引用解析=").Append(verdict).Append(detail.Length > 0 ? "（" + detail + "）" : "");
            MelonLogger.Msg(sb.ToString());
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[GSM] 判定字段转储失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>递归检查（含嵌套对象，如 `CompatibleCards.TriggerCardsWarpData`）—— 拖拽判定字段就在这一层。</summary>
    private static void CheckRefsRec(object obj, KVProvider json, string prefix, List<string> bad, ref int ok, int depth)
    {
        if (depth > 3 || obj == null || json == null || !json.IsObject) return;
        try
        {
            foreach (var k in json.Keys)
            {
                if (k.EndsWith("WarpData"))
                {
                    var fld = k.Substring(0, k.Length - 8);
                    var target = Member(obj, fld);
                    var cnt = (int)ElemCount(target);
                    var n = json[k].IsArray ? json[k].Count : 1;
                    if (n > 0 && cnt == 0) bad.Add(prefix + fld + "=0/" + n);
                    else ok++;
                    continue;
                }

                var child = json[k];
                if (child != null && child.IsObject)
                {
                    var sub = Member(obj, k);
                    if (sub != null) CheckRefsRec(sub, child, prefix + k + ".", bad, ref ok, depth + 1);
                }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }
    /// <summary>递归找 JSON 里所有 `*WarpData` 键，检查目标字段是否非空 → 返回 OK/失败 + 明细。</summary>
    private static string CheckRefsResolved(object obj, KVProvider json, out string detail)
    {
        detail = "";
        try
        {
            if (obj == null || json == null || !json.IsObject) return "OK";
            var bad = new List<string>();
            var ok = 0;
            CheckRefsRec(obj, json, "", bad, ref ok, 0);

            if (bad.Count > 0)
            {
                detail = string.Join(",", bad);
                return "失败";
            }

            return ok > 0 ? "OK(" + ok + ")" : "OK";
        }
        catch (Exception e)
        {
            detail = e.GetType().Name;
            return "未知";
        }
    }
    // ═══════════ 调用来源追踪（定位"谁重建了数组"） ═══════════
    /// <summary>当前阶段标记（反序列化 / 只解引用 / MODIFY就地改 / 新建元素追加 …）。</summary>
    public static string CurrentPhase = "初始";

    /// <summary>最近一次"对象元素数组"调用的来源（函数 + 前 3 帧）。</summary>
    public static string LastArrayCallSite = "?";

    /// <summary>取调用栈前 N 帧的方法名（诊断"到底谁触发了这次重建"）。</summary>
    public static string Frames(int n = 3)
    {
        try
        {
            var st = new System.Diagnostics.StackTrace(1, false);
            var parts = new List<string>();
            for (var i = 0; i < st.FrameCount && parts.Count < n; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null) continue;
                parts.Add(m.DeclaringType?.Name + "." + m.Name);
            }

            return string.Join(" ← ", parts);
        }
        catch
        {
            return "?";
        }
    }
    // ═══════════ 新增/就地改元素的「反序列化 + 三行证据」（Lead 指定） ═══════════
    public static int DeserOk, DeserFail;
    private static int DeserEvidenceLogged;

    /// <summary>
    /// 给"新建/就地改"的对象元素填字段：**无条件先走已确认可用的 ICall**
    /// `UnityEngine.JsonUtility::FromJsonInternal`（纯 ICall、零裸内存、不受任何开关门控），
    /// 失败才退回托管 `FromJsonOverwrite`。并按 Lead 要求打**三行证据**：
    /// ① json 前 300 字符 ② `il2cpp_class_get_type` 拿到的类名 ③ 调用后**立刻读回** `ActionName.DefaultText`。
    /// </summary>
    public static void DeserializeElement(Il2CppObjectBase target, KVProvider json, string how)
    {
        try
        {
            if (target == null || json == null) return;
            var savedPhase = CurrentPhase;
            CurrentPhase = "反序列化(" + how + ")";
            var elJson = json.ToJson();
            var ptr = target.Pointer;
            var clsName = AsciiClassName(ptr);
            var before = ReadName(target);

            var ok = FromJsonViaIcall(elJson, target);
            var after = ReadName(target);

            if (DeserEvidenceLogged < 6)
            {
                DeserEvidenceLogged++;
                MelonLogger.Msg("[DESER] " + how + " 元素类型=" + clsName
                                + " | ①json前300=" + (elJson.Length > 300 ? elJson.Substring(0, 300) : elJson));
                MelonLogger.Msg("[DESER] " + how + " ②il2cpp类=" + clsName
                                + " ③调用后立即读回 ActionName.DefaultText=\"" + after + "\"（调用前=\"" + before + "\"）"
                                + " ICall=" + ok);
            }

            if (ok && after == before && !string.IsNullOrEmpty(after) == false && after.Length == 0)
            {
                // ICall 成功但没写进去 → 用托管版再试一次（异常原文照打）
                try
                {
                    UnityEngine.JsonUtility.FromJsonOverwrite(elJson, target.Cast<Il2CppSystem.Object>());
                    var again = ReadName(target);
                    if (DeserEvidenceLogged < 8)
                    {
                        DeserEvidenceLogged++;
                        MelonLogger.Msg("[DESER] 托管版兜底后读回 ActionName.DefaultText=\"" + again + "\"");
                    }
                }
                catch (Exception de)
                {
                    if (DeserFail < 5)
                        MelonLogger.Warning("[DESER] 托管版反序列化**抛异常**: " + de.GetType().Name + " " + de.Message);
                }
            }

            CurrentPhase = savedPhase;
            if (ok) DeserOk++;
            else DeserFail++;
        }
        catch (Exception e)
        {
            DeserFail++;
            if (DeserFail <= 3) MelonLogger.Warning("[DESER] 反序列化异常: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>该字段的元素类型是否含指定字段（用于"这条追加项到底该不该有 ActionName"的判空口径）。</summary>
    public static bool ElementTypeHasField(object host, string field, string probe)
    {
        try
        {
            var gen = WarpperClassGen.MainGen.GetOrGen(host.GetType());
            if (!gen.TryGetValue(field, out var tuple) || tuple.fldType == null) return false;
            var ft = tuple.fldType;
            Type elem = null;
            if (IsIl2CppArrayType(ft))
            {
                elem = ft.IsGenericType ? ft.GetGenericArguments()[0] : ft.GetElementType();
            }
            else if (ft.IsGenericType)
            {
                elem = ft.GetGenericArguments()[0];
            }

            if (elem == null) return false;
            var egen = WarpperClassGen.MainGen.GetOrGen(elem);
            return egen != null && egen.ContainsKey(probe);
        }
        catch
        {
            return false;
        }
    }
    /// <summary>从 Il2CppClass* 取 ASCII 类名。</summary>
    public static string AsciiClassNameOf(IntPtr cls)
    {
        try
        {
            if (cls == IntPtr.Zero) return "?";
            var n = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(cls);
            return n == IntPtr.Zero ? "?" : (System.Runtime.InteropServices.Marshal.PtrToStringAnsi(n) ?? "?");
        }
        catch
        {
            return "?";
        }
    }
    /// <summary>ASCII 类名（`il2cpp_class_get_name` + ANSI 解码）—— 避免日志里出现 `?x` 这类乱码。</summary>
    public static string AsciiClassName(IntPtr objPtr)
    {
        try
        {
            if (objPtr == IntPtr.Zero) return "?";
            var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(objPtr);
            if (cls == IntPtr.Zero) return "?";
            var namePtr = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(cls);
            return namePtr == IntPtr.Zero ? "?" : (Marshal.PtrToStringAnsi(namePtr) ?? "?");
        }
        catch
        {
            return "?";
        }
    }
    /// <summary>读 `ActionName.DefaultText`（内联结构里的字符串），用于"写进去没有"的客观判据。</summary>
    public static string ReadName(object element)
    {
        try
        {
            var an = Member(element, "ActionName");
            if (an == null) return "<无ActionName字段>";
            var txt = Member(an, "DefaultText")?.ToString();
            if (!string.IsNullOrEmpty(txt)) return txt;
            var lk = Member(an, "LocalizationKey")?.ToString();
            return string.IsNullOrEmpty(lk) ? "" : lk;
        }
        catch (Exception e)
        {
            return "<读取异常:" + e.GetType().Name + ">";
        }
    }
    // ═══════════ JsonUtility：直接走已确认可用的 ICall（安全，无裸内存操作） ═══════════
    public static int FromJsonIcallOk, FromJsonIcallFail;

    /// <summary>
    /// `UnityEngine.JsonUtility::FromJsonInternal(json, objectToOverwrite, type)`（ICALL-AVAILABLE 确认 impl=0x17A3C0）。
    /// 为什么绕开托管版 `FromJsonOverwrite`：托管版的第三个参数（目标类型）由**包装类型**推断，
    /// 一旦被 `Cast&lt;Il2CppSystem.Object&gt;()` 包过就变成 `System.Object` → 一个字段都写不进（真机实测）。
    /// 这里显式传 `il2cpp_type_get_object(il2cpp_class_get_type(真实类))` —— 纯 ICall，无内存直写。
    /// </summary>
    public static bool FromJsonViaIcall(string json, Il2CppObjectBase obj)
    {
        try
        {
            if (obj == null || string.IsNullOrEmpty(json)) return false;
            var fn = RawTexture.ResolveIcall("UnityEngine.JsonUtility::FromJsonInternal");
            if (fn == IntPtr.Zero)
            {
                FromJsonIcallFail++;
                return false;
            }

            var ptr = obj.Pointer;
            var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(ptr);
            if (cls == IntPtr.Zero) { FromJsonIcallFail++; return false; }
            var typeObj = Il2CppInterop.Runtime.IL2CPP.il2cpp_type_get_object(
                Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_type(cls));
            var str = Il2CppInterop.Runtime.IL2CPP.il2cpp_string_new(json);
            if (typeObj == IntPtr.Zero || str == IntPtr.Zero) { FromJsonIcallFail++; return false; }

            unsafe
            {
                var del = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)fn;
                del(str, ptr, typeObj);
            }

            FromJsonIcallOk++;
            return true;
        }
        catch (Exception e)
        {
            FromJsonIcallFail++;
            if (FromJsonIcallFail <= 3)
                MelonLogger.Warning("[ARR] FromJsonInternal 调用失败: " + e.GetType().Name + " " + e.Message);
            return false;
        }
    }
    // ═══════════ 值类型字段：**只走托管属性 setter**（安全通道） ═══════════
    //  ⚠ 2026-10-03 真机教训：按"基址+偏移"直写非托管内存会在启动期 SIGSEGV（写坏内存），
    //    整条直写路径已撤销，永不恢复。这里只做托管属性赋值 —— 由运行时保证类型/GC 安全。
    public static int SafeSetterOk, SafeSetterFail;

    /// <summary>
    /// 值类型字段的安全写入：只用 **生成的托管属性 setter**。
    /// · 基础类型/枚举/字符串 → `Convert.ChangeType` 后 `prop.SetValue`；
    /// · 内联结构（如 `LocalizedString`）→ 取结构的托管代理，对**子字段**继续用属性 setter，并**读回验证**是否生效
    ///  （不生效只记日志，不做任何裸内存写入）。
    /// </summary>
    public static bool TrySetManagedField(object host, string fld, KVProvider v)
    {
        try
        {
            if (host == null) return false;
            var prop = host.GetType().GetProperty(fld);
            if (prop == null || !prop.CanWrite) return false;   // 没有 setter = 不适用，不计数（避免 12 万条假失败）
            var pt = prop.PropertyType;

            if (v.IsObject)
            {
                // 内联结构：拿到托管代理后对子字段赋值，再读回验证
                var sub = prop.GetValue(host);
                if (sub == null) return false;
                var any = false;
                foreach (var k in v.Keys)
                {
                    var sp = sub.GetType().GetProperty(k);
                    if (sp == null || !sp.CanWrite) continue;
                    if (AssignScalar(sp, sub, v[k])) any = true;
                }

                if (!any) return false;
                var readBack = prop.GetValue(host);
                var ok = readBack != null && DescribeValue(readBack) != DescribeValue(sub);
                if (SetterVerifyLogged < 10)
                {
                    SetterVerifyLogged++;
                    MelonLogger.Msg("[INL] 内联结构经属性写入: " + host.GetType().Name + "." + fld
                                    + " 子字段写入=" + any + " 读回生效=" + ok + " 读回值=" + DescribeValue(readBack));
                }

                if (ok) SafeSetterOk++;
                else SafeSetterFail++;
                return ok;
            }

            if (v.IsArray || (!v.IsObject && !v.IsString && !v.IsInt && !v.IsBoolean)) return false;   // 不适用，不计数

            if (AssignScalar(prop, host, v))
            {
                SafeSetterOk++;
                if (SetterVerifyLogged < 10)
                {
                    SetterVerifyLogged++;
                    MelonLogger.Msg("[INL] 值类型属性已写: " + host.GetType().Name + "." + fld
                                    + " = " + v.ToString() + " 读回=" + prop.GetValue(host));
                }

                return true;
            }
        }
        catch (Exception e)
        {
            if (SafeSetterSamples.Count < 10) SafeSetterSamples.Add(host?.GetType().Name + "." + fld + " : " + e.Message);
        }

        SafeSetterFail++;
        return false;
    }

    private static bool AssignScalar(System.Reflection.PropertyInfo p, object target, KVProvider v)
    {
        try
        {
            var pt = p.PropertyType;
            object val;
            if (pt == typeof(string)) val = v.IsString ? v.String : v.ToString().Trim('"');
            else if (pt == typeof(bool)) val = v.IsBoolean ? v.String.Equals("true", StringComparison.OrdinalIgnoreCase) : bool.Parse(v.String);
            else if (pt == typeof(float)) val = Convert.ToSingle(v.IsInt ? (object)v.Int : double.Parse(v.String, System.Globalization.CultureInfo.InvariantCulture));
            else if (pt == typeof(double)) val = v.IsInt ? (double)v.Int : double.Parse(v.String, System.Globalization.CultureInfo.InvariantCulture);
            else if (pt == typeof(long)) val = v.IsInt ? (long)v.Int : long.Parse(v.String, System.Globalization.CultureInfo.InvariantCulture);
            else if (pt == typeof(int) || pt.IsEnum) val = v.IsInt ? v.Int : int.Parse(v.String, System.Globalization.CultureInfo.InvariantCulture);
            else return false;
            p.SetValue(target, val);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int SetterVerifyLogged;
    public static readonly List<string> SafeSetterSamples = new();
    // ═══════════ GSM 逐条「内容级」判据（Lead 指定：证据不许含糊） ═══════════

    /// <summary>从一个 GSM JSON 里取出被改字段名与 WarpType（键形如 `XxxWarpData` + `XxxWarpType`）。</summary>
    public static List<(string Field, int WarpType, int ElemCount, string ElemKind)> GsmFieldsOf(KVProvider json)
    {
        var list = new List<(string, int, int, string)>();
        try
        {
            foreach (var k in json.Keys)
            {
                if (!k.EndsWith("WarpType")) continue;
                var fld = k.Substring(0, k.Length - 8);
                var wt = 0;
                try { if (json[k].IsInt) wt = json[k].Int; } catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                var n = 0;
                var kind = "?";
                if (json.ContainsKey(fld + "WarpData"))
                {
                    var d = json[fld + "WarpData"];
                    if (d.IsArray)
                    {
                        n = d.Count;
                        if (n > 0) kind = d[0].IsObject ? "obj" : (d[0].IsString ? "str" : "?");
                    }
                    else if (d.IsString) { n = 1; kind = "str"; }
                }

                list.Add((fld, wt, n, kind));
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return list;
    }

    /// <summary>字段内容快照：容器字段 → "N 项: 名称1|名称2…"；标量/引用 → ToString()/名字。</summary>
    public static Dictionary<string, string> FieldSnapshot(object obj, List<string> fields)
    {
        var d = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            try
            {
                if (IsContainerField(obj, f))
                {
                    d[f] = ContainerDetails(obj, f, 3);
                }
                else
                {
                    var v = WarpperClassGen.MainGenTools.CommonGet(obj, f);
                    d[f] = DescribeValue(v);
                }
            }
            catch (Exception e)
            {
                d[f] = "<读取失败:" + e.GetType().Name + ">";
            }
        }

        return d;
    }

    /// <summary>
    /// 元素级明细（问题 2 判定用）：原生指针 + 关键字段值。
    /// 若同一元素改前/改后这些值完全一致 → 只是打印问题；有任何差异 → 真损坏。
    /// </summary>
    /// <summary>该字段是不是"数组/List"（决定用条目明细还是标量值来描述）。</summary>
    public static bool IsContainerField(object obj, string fld)
    {
        try
        {
            var gen = WarpperClassGen.MainGen.GetOrGen(obj.GetType());
            if (!gen.TryGetValue(fld, out var tuple)) return false;
            var ft = tuple.fldType;
            if (ft == null) return false;
            if (IsIl2CppArrayType(ft)) return true;
            return ft.IsGenericType &&
                   ft.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>);
        }
        catch
        {
            return false;
        }
    }
    public static string ElementDetails(object container, int idx)
    {
        try
        {
            var e = Retype(GetElem(container, idx));
            if (e == null) return "[" + idx + "]=<null>";
            var ptr = PtrOf(e).ToInt64().ToString("X");
            var an = Member(e, "ActionName");
            var txt = an == null ? "-" : (Member(an, "DefaultText")?.ToString() ?? "-");
            var cost = Member(e, "DaytimeCost")?.ToString() ?? "-";
            var prod = Member(e, "ProducedCards");
            var reqd = Member(e, "RequiredCardsOnBoard");
            var reqT = Member(e, "RequiredTagsOnBoard");
            return "[" + idx + "] ptr=0x" + ptr + " ActionName=\"" + txt + "\" DaytimeCost=" + cost
                   + " ProducedCards=" + (int)ElemCount(prod) + " RequiredCardsOnBoard=" + (int)ElemCount(reqd)
                   + " RequiredTagsOnBoard=" + (int)ElemCount(reqT);
        }
        catch (Exception e2)
        {
            return "[" + idx + "]=<明细失败:" + e2.GetType().Name + ">";
        }
    }

    /// <summary>一个容器字段的"逐元素明细"（最多前 N 个），用于改前/改后对比。</summary>
    public static string ContainerDetails(object obj, string field, int max = 3)
    {
        try
        {
            var v = WarpperClassGen.MainGenTools.CommonGet(obj, field);
            var n = (int)ElemCount(v);
            if (n <= 0) return "(" + n + " 项)";
            var parts = new List<string>();
            for (var i = 0; i < n && i < max; i++) parts.Add(ElementDetails(v, i));
            return "(" + n + " 项) " + string.Join(" ; ", parts);
        }
        catch (Exception e)
        {
            return "<明细失败:" + e.GetType().Name + ">";
        }
    }
    /// <summary>容器 → 条目数 + 每个元素的"人话名字"（ActionName.DefaultText / CardName.DefaultText / name）。</summary>
    public static string DescribeValue(object v)
    {
        if (v == null) return "<null>";
        try
        {
            var n = (int)ElemCount(v);
            if (n > 0)
            {
                var parts = new List<string>();
                for (var i = 0; i < n && i < 8; i++)
                    parts.Add(ElemName(GetElem(v, i)));
                return n + " 项[" + string.Join(" | ", parts) + (n > 8 ? " | …" : "") + "]";
            }

            if (n == 0 && (IsIl2CppArrayType(v.GetType()) || v.GetType().Name.Contains("List")))
                return "0 项[]";

            var s = v.ToString();
            if (!string.IsNullOrEmpty(s) && s != v.GetType().FullName) return s;
            var nm = NameOf(v);
            if (!string.IsNullOrEmpty(nm)) return nm;
            return "0x" + PtrOf(v).ToInt64().ToString("X");
        }
        catch
        {
            return "<描述失败>";
        }
    }

    private static string ElemName(object e)
    {
        if (e == null) return "<null>";
        try
        {
            // 打印副作用修复：数组被重建后，元素代理可能退化成基类/Il2CppSystem.Object，
            // 于是 ActionName 等属性取不到、回退成类型名（真机实测 6 项[Ignore it|…] → 6 项[DismantleCardAction×6]）。
            e = Retype(e);
            var an = Member(e, "ActionName");                       // CardAction / DismantleCardAction
            if (an != null)
            {
                var s = Member(an, "DefaultText")?.ToString();
                if (!string.IsNullOrEmpty(s)) return s;
                var lk = Member(an, "LocalizationKey")?.ToString();
                if (!string.IsNullOrEmpty(lk)) return lk;
            }

            var cn = Member(e, "CardName");                         // CardData
            if (cn != null)
            {
                var s = Member(cn, "DefaultText")?.ToString();
                if (!string.IsNullOrEmpty(s)) return s;
            }

            var nm = Member(e, "name")?.ToString();
            if (!string.IsNullOrEmpty(nm)) return nm;
            var id = Member(e, "UniqueID")?.ToString();
            if (!string.IsNullOrEmpty(id)) return id.Substring(0, Math.Min(8, id.Length));
            return Cls(PtrOf(e)) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// 逐条 GSM 判据（不设上限，63 条全打）：
    /// `[GSM] 目标[7/63] CardData/LemonGrass guid=ab12cd34 字段=DismantleActions WarpType=4 字段存在=True`
    /// `[GSM]   DismantleActions: 2 项[…] → 3 项[…] ✓`
    /// 未命中时追加一行 warp 对详情（键名/类型/元素数/元素类型），用于切开"JSON 没解析出键"与"没落到字段"。
    /// </summary>
    public static void LogGsmEntry(int idx, int total, object obj, string guid, KVProvider json,
        Dictionary<string, string> before)
    {
        try
        {
            var cls = Cls(obj is Il2CppObjectBase ib ? ib.Pointer : IntPtr.Zero);
            var name = NameOf(obj);
            var g8 = string.IsNullOrEmpty(guid) ? "?" : guid.Substring(0, Math.Min(8, guid.Length));
            var gen = WarpperClassGen.MainGen.GetOrGen(obj.GetType());

            var fields = GsmFieldsOf(json);
            var changedAny = false;

            foreach (var (fld, wt, cnt, kind) in fields)
            {
                var exists = gen.ContainsKey(fld);
                var after = FieldSnapshot(obj, new List<string> { fld })[fld];
                before.TryGetValue(fld, out var b);
                b ??= "<未采样>";
                var changed = b != after;
                if (changed) changedAny = true;
                MelonLogger.Msg("[GSM] 目标[" + idx + "/" + total + "] " + cls + "/" + name + " " + g8
                                + " 字段=" + fld + " WarpType=" + wt + "(" + WarpTypeName(wt) + ")"
                                + " 字段存在=" + exists + " JSON元素=" + cnt + "(" + kind + ")");
                MelonLogger.Msg("[GSM]   " + fld + ": " + b + " → " + after + (changed ? " ✓" : "  ⚠未变化"));
                // 问题2 判定：逐元素"原生指针 + 关键字段值"改前/改后对比
                //  （值完全一致 = 只是打印问题；有差异 = 真损坏，必须只追加不重建）
                if (IsContainerField(obj, fld))
                {
                    var beforeObj = WarpperClassGen.MainGenTools.CommonGet(obj, fld);
                    MelonLogger.Msg("[GSM]     " + fld + " 明细改后: " + ContainerDetails(obj, fld, 3));
                    var bl = b;
                    MelonLogger.Msg("[GSM]     " + fld + " 明细改前: " + bl);
                }
            }

            // 记录"新增段"供后置复读（条数增长 = 尾部新增）
            try
            {
                foreach (var (fld2, _, _, _) in fields)
                {
                    var bCnt = CountOfBefore(before, fld2);
                    var aCnt = (int)ElemCount(WarpperClassGen.MainGenTools.CommonGet(obj, fld2));
                    if (aCnt > bCnt) GsmAppended.Add((obj, fld2, bCnt));
                }
            }
            catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

            if (changedAny)
            {
                GsmApplied++;
                if (!string.IsNullOrEmpty(guid)) IntentionalGuids.Add(guid);
                try { if (obj is Il2CppObjectBase ib2) IntentionalTargets.Add(ib2.Pointer); } catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                IntentionalLog.Add("[GSM] " + cls + "/" + name + "(" + g8 + ") 已改 " + fields.Count + " 个字段");
            }
            else
            {
                GsmNoChange++;
                // 未变化 → 把消费端实际收到的 warp 对原样打出来（Lead 指定的一刀切证据）
                foreach (var (fld, wt, cnt, kind) in fields)
                {
                    var has = json.ContainsKey(fld + "WarpData");
                    var dataKind = "缺失";
                    if (has)
                    {
                        var d = json[fld + "WarpData"];
                        dataKind = d.IsArray ? ("数组(" + d.Count + " 元素, 首元素="
                                                + (d.Count > 0 ? (d[0].IsObject ? "对象" : d[0].IsString ? "字符串" : "其它") : "空")
                                                + ")")
                            : d.IsString ? "字符串" : d.IsObject ? "对象" : "其它";
                    }

                    MelonLogger.Msg("[GSM]   ⚠ warp对: " + fld + "WarpData 存在=" + has + " 类型=" + dataKind
                                    + " ; " + fld + "WarpType=" + wt + " 字段在gen表=" + gen.ContainsKey(fld)
                                    + " 对象类型=" + obj.GetType().Name);
                }
            }
        }
        catch (Exception e)
        {
            GsmFailed++;
            MelonLogger.Warning("[GSM] 判据打印失败(" + guid + "): " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>从"改前明细"字符串里解析出条目数（形如 "(6 项) …"）。</summary>
    private static int CountOfBefore(Dictionary<string, string> before, string field)
    {
        try
        {
            if (!before.TryGetValue(field, out var s) || string.IsNullOrEmpty(s)) return -1;
            var i = s.IndexOf(" 项", StringComparison.Ordinal);
            if (i <= 0) return -1;
            var j = s.LastIndexOf('(', i);
            if (j < 0) return -1;
            return int.Parse(s.Substring(j + 1, i - j - 1));
        }
        catch
        {
            return -1;
        }
    }

    private static string WarpTypeName(int wt)
    {
        return wt switch
        {
            0 => "NONE",
            1 => "COPY",
            2 => "CUSTOM",
            3 => "REFERENCE",
            4 => "ADD",
            5 => "MODIFY",
            6 => "ADD_REFERENCE",
            _ => "?"
        };
    }
    // ─────────────────── GSM（改造游戏自带卡牌）判据与"有意修改"登记 ───────────────────
    /// <summary>本轮**有意**修改过的游戏对象（GSM 目标）—— INVARIANT 采样要排除它们，并单独列清单。</summary>
    public static readonly HashSet<IntPtr> IntentionalTargets = new();
    /// <summary>同上，按 GUID 记（INVARIANT 快照是按 `类|GUID` 存的，排除时用得上）。</summary>
    public static readonly HashSet<string> IntentionalGuids = new();
    public static readonly List<string> IntentionalLog = new();
    public static int GsmApplied, GsmNoChange, GsmFailed;

    /// <summary>取对象上所有「数组/List」字段的条目数（GSM 改造前后对比用）。</summary>
    public static Dictionary<string, int> ContainerCountsOf(object obj)
    {
        var d = new Dictionary<string, int>();
        if (obj == null) return d;
        try
        {
            foreach (var kv in WarpperClassGen.MainGen.GetOrGen(obj.GetType()))
            {
                var ft = kv.Value.fldType;
                if (ft == null) continue;
                var isList = false;
                try { isList = ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>); }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                if (!isList && !IsIl2CppArrayType(ft)) continue;
                try
                {
                    var v = WarpperClassGen.MainGenTools.CommonGet(obj, kv.Key);
                    d[kv.Key] = (int)ElemCount(v);
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

        return d;
    }

    /// <summary>
    /// `[GSM] 名字(guid): 字段 X→Y ✓`：逐条打印**改前→改后**数字（Lead 指定的客观判据），
    /// 并把该对象登记为"有意修改"（供 INVARIANT 排除 + 清单输出）。
    /// </summary>
    public static void LogGsmChange(object obj, string guid, Dictionary<string, int> before)
    {
        try
        {
            var after = ContainerCountsOf(obj);
            var changes = new List<string>();
            foreach (var kv in after)
            {
                before.TryGetValue(kv.Key, out var b);
                if (b != kv.Value) changes.Add(kv.Key + " " + b + "→" + kv.Value);
            }

            foreach (var kv in before)
                if (!after.ContainsKey(kv.Key))
                    changes.Add(kv.Key + " " + kv.Value + "→(字段消失)");

            if (!string.IsNullOrEmpty(guid)) IntentionalGuids.Add(guid);
            var name = NameOf(obj);
            var cls = Cls(obj is Il2CppObjectBase ib ? ib.Pointer : IntPtr.Zero);
            var g8 = string.IsNullOrEmpty(guid) ? "?" : guid.Substring(0, Math.Min(8, guid.Length));

            if (changes.Count > 0)
            {
                GsmApplied++;
                var line = "[GSM] " + cls + " " + name + "(" + g8 + "): " + string.Join(" / ", changes) + " ✓";
                MelonLogger.Msg(line);
                IntentionalLog.Add(line);
                try { if (obj is Il2CppObjectBase ib2) IntentionalTargets.Add(ib2.Pointer); } catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }
            else
            {
                GsmNoChange++;
                MelonLogger.Msg("[GSM] " + cls + " " + name + "(" + g8 + "): 无字段条目变化"
                                + "（JSON 键=" + (before.Count) + " 个容器字段已比对；可能是标量/引用类字段改动）");
            }
        }
        catch (Exception e)
        {
            GsmFailed++;
            MelonLogger.Warning("[GSM] 判据打印失败(" + guid + "): " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// `[GSM] 判据覆盖`：47 个目标**逐条**都做了"改前/改后"比对（这是真正的覆盖），
    /// 而不是靠在 76 个对象的小样本里碰运气（真机实测采样内命中=0，因为 20 个/类的采样撞不上那 47 张卡）。
    /// </summary>
    public static void DumpGsmCoverage()
    {
        try
        {
            var total = GsmApplied + GsmNoChange;
            MelonLogger.Msg("[GSM] 判据覆盖: 逐条前后比对=" + total + " 条（有意变化=" + GsmApplied
                            + " 无变化=" + GsmNoChange + " 失败=" + GsmFailed + "）"
                            + "；被改对象=" + IntentionalGuids.Count + " 个"
                            + "；[INVARIANT] 小样本(每类≤20/共76)内命中=" + GsmSampleHit
                            + "（样本撞不上属正常，覆盖以本行为准）");
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>GSM 追加出来的元素（宿主对象 / 字段 / 新增段起始下标），供"全部 warp 完成后"的后置复读。</summary>
    public static readonly List<(object Host, string Field, int FirstNew)> GsmAppended = new();

    /// <summary>
    /// `[GSM] 后置复读 <宿主>.<字段>[i]: ActionName="…" DaytimeCost=… ProducedCards=…`
    /// —— 在**全部 warp 完成之后**再读一次追加元素，用来排除"打印时机"造成的假空字段。
    /// </summary>
    public static void DumpGsmAppendedReadback()
    {
        try
        {
            var empty = 0;
            var withName = 0;
            var withoutName = 0;
            foreach (var (host, field, firstNew) in GsmAppended)
            {
                var hn = NameOf(host) ?? "?";
                var details = ContainerDetails(host, field, 6);
                MelonLogger.Msg("[GSM] 后置复读 " + hn + "." + field + "（新增自下标 " + firstNew + "）: " + details);

                // ★ [2026-10-03 第 4 轮] 只统计**确实含 ActionName 字段**的元素：大部分追加项是掉落/概率类
                //   （`CardDropChanceModifiers`/`ProducedCards`），根本没有 ActionName → 原来被算成"空"= 假警报。
                if (ElementTypeHasField(host, field, "ActionName"))
                {
                    withName++;
                    if (details.Contains("ActionName=\"-\"")) empty++;
                }
                else
                {
                    withoutName++;
                }
            }

            MelonLogger.Msg("[GSM] 后置复读汇总: 含 ActionName 的字段=" + withName + " 个（其中仍为空=" + empty + "）"
                            + "；无 ActionName 字段=" + withoutName + " 个（掉落/概率类，不参与判空）"
                            + (withName > 0 && empty == 0
                                ? " ✓ 追加元素字段已写入"
                                : withName == 0
                                    ? "（本轮没有含 ActionName 的追加项）"
                                    : " ⚠ 仍有空字段（见上）"));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[GSM] 后置复读失败: " + e.GetType().Name + " " + e.Message);
        }
    }
    /// <summary>[INVARIANT] 小样本里刚好撞上 GSM 目标的个数（由 CompareGameContainers 统计）。</summary>
    public static int GsmSampleHit;
    /// <summary>`[GSM] 有意修改的游戏卡片清单` + 计数（供 Lead 区分"有意改动"与"意外污染"）。</summary>
    public static void DumpIntentionalSummary()
    {
        try
        {
            MelonLogger.Msg("[GSM] 有意修改汇总: 成功=" + GsmApplied + " 无字段变化=" + GsmNoChange
                            + " 失败=" + GsmFailed + " 涉及对象=" + IntentionalTargets.Count);
            for (var i = 0; i < IntentionalLog.Count && i < 70; i++)
                MelonLogger.Msg("[GSM]   有意修改[" + (i + 1) + "] " + IntentionalLog[i].Replace("[GSM] ", ""));
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>把注册表对象安全转成具体代理类型（GetType() 常常只返回 UniqueIDScriptable）。</summary>
    public static T CastOrNull<T>(object o) where T : Il2CppObjectBase
    {
        if (o == null) return null;
        try
        {
            if (o is T t) return t;
            return ((Il2CppObjectBase)o).TryCast<T>();
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DIAG] CastOrNull<" + typeof(T).Name + "> 失败: " + e.GetType().Name + " " + e.Message);
            return null;
        }
    }

    /// <summary>打印某个 interop 类型在 MainGen 里生成出来的字段名（= 真实 C# 字段名）。</summary>
    public static void DumpGenFields(Type t)
    {
        if (!MiniLoader.DiagFull) { EventCardDumps++; return; }
        try
        {
            var g = WarpperClassGen.MainGen.GetOrGen(t);
            MelonLogger.Msg("[GEN] " + t.Name + " 生成字段(" + g.Count + "): " + string.Join(", ", g.Keys));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[GEN] " + t.Name + " 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>反射打印代理类型自己声明的属性（只读，逐项 try/catch）。</summary>
    public static void Describe(object o, Type t, int maxProps)
    {
        if (o == null || t == null) return;
        MelonLogger.Msg("[DESC] " + t.Name + " name=" + NameOf(o) + " GUID=" + SafeUniqueId(o));
        var props = t.GetProperties(System.Reflection.BindingFlags.Public |
                                   System.Reflection.BindingFlags.Instance |
                                   System.Reflection.BindingFlags.DeclaredOnly);
        int n = 0;
        foreach (var p in props)
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.Name is "Pointer" or "ObjectClass" or "WasCollected" or "ObjectDied") continue;
            if (++n > maxProps) break;
            string val;
            try
            {
                var v = p.GetValue(o);
                val = Render(v);
            }
            catch (Exception e)
            {
                val = "<读取失败:" + e.GetType().Name + " " + (e.Message ?? "").Split('\n')[0] + ">";
            }

            MelonLogger.Msg("[DESC]   " + p.Name + " (" + ShortType(p.PropertyType) + ") = " + val);
        }
    }

    private static string ShortType(Type t)
    {
        try
        {
            if (t.IsGenericType)
                return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(ShortType)) + ">";
            return t.Name;
        }
        catch { return "?"; }
    }

    /// <summary>把一个属性值渲染成短字符串（数组/列表给长度 + 前几个元素名）。</summary>
    public static string Render(object v)
    {
        if (v == null) return "<null>";
        try
        {
            if (v is string s) return "\"" + (s.Length > 60 ? s.Substring(0, 60) + "…" : s) + "\"";
            if (v is Il2CppObjectBase b)
            {
                // Il2CppSystem 的 List<T> 不实现 System.Collections.IEnumerable，用反射取 Count/索引器
                try
                {
                    var vt = v.GetType();
                    var pc = vt.GetProperty("Count");
                    var mc = pc == null ? vt.GetMethod("Count", Type.EmptyTypes) : null;
                    var idx = vt.GetProperty("Item", new[] { typeof(int) });
                    if ((pc != null || mc != null) && idx != null)
                    {
                        var n = pc != null ? (int)pc.GetValue(v) : (int)mc.Invoke(v, null);
                        var items = new System.Collections.Generic.List<string>();
                        for (var i = 0; i < n && i < 6; i++)
                        {
                            var it = idx.GetValue(v, new object[] { i });
                            items.Add(NameOf(it) + "/" + Cls(it));
                        }

                        return "[" + n + " 项] " + string.Join(", ", items);
                    }
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }

                var arr = v as System.Collections.IEnumerable;
                if (arr != null && !(v is string))
                {
                    var items = new System.Collections.Generic.List<string>();
                    int cnt = 0;
                    foreach (var it in arr)
                    {
                        cnt++;
                        if (items.Count < 4) items.Add(NameOf(it) + "/" + Cls(it));
                        if (cnt >= 40) break;
                    }

                    if (cnt > 0 || Cls(v).EndsWith("[]"))
                        return "[" + cnt + " 项] " + string.Join(", ", items);
                }

                return Cls(v) + " name=" + NameOf(v);
            }

            var str = v.ToString() ?? "<null-str>";
            return str.Length > 80 ? str.Substring(0, 80) + "…" : str;
        }
        catch (Exception e)
        {
            return "<渲染失败:" + e.GetType().Name + ">";
        }
    }

    /// <summary>打印 PerkTabGroup 的结构（决定游戏 UI 到底从哪里枚举特质组）。</summary>
    public static void DumpPerkTabGroups(int max)
    {
        var list = RegistryByClass("PerkTabGroup", max);
        MelonLogger.Msg("[PTG] 注册表 PerkTabGroup = " + list.Count + " 个");
        foreach (var kv in list)
        {
            try
            {
                var o = CastOrNull<PerkTabGroup>(kv.Value);
                if (o == null)
                {
                    MelonLogger.Warning("[PTG] TryCast 失败 key=" + kv.Key);
                    continue;
                }

                Describe(o, typeof(PerkTabGroup), 24);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PTG] 转储失败: " + e.GetType().Name + " " + e.Message);
            }
        }
    }

    /// <summary>打印 mod 自己的 PerkGroup（warp 之后）真实字段值：PerksList 到底有没有被写进去。</summary>
    public static void DumpModPerkGroups()
    {
        try
        {
            var d = ItemDictionary(typeof(PerkGroup));
            MelonLogger.Msg("[PG] ItemDictionary(PerkGroup) = " + d.Count + " 个");
            foreach (var kv in d)
            {
                try
                {
                    var pg = kv.Value as PerkGroup;
                    if (pg == null)
                    {
                        MelonLogger.Warning("[PG] " + kv.Key + " 不是 PerkGroup: " + Cls(kv.Value));
                        continue;
                    }

                    Describe(pg, typeof(PerkGroup), 12);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[PG] 转储失败 " + kv.Key + ": " + e.GetType().Name + " " + e.Message);
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[PG] 失败: " + e.Message);
        }
    }

    /// <summary>打印一段 mod JSON 的键值。</summary>
    public static void DumpJson(string tag, KVProvider json, int max)
    {
        if (!MiniLoader.DiagFull) { EventCardDumps++; return; }
        try
        {
            MelonLogger.Msg("[JSON] " + tag + " 字段数=" + json.Count);
            foreach (var k in json.Keys)
            {
                string v;
                try { v = RenderKv(json[k]); }
                catch (Exception e) { v = "<err " + e.GetType().Name + ">"; }
                MelonLogger.Msg("[JSON]   " + k + " = " + v);
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[JSON] " + tag + " 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    private static string RenderKv(KVProvider v)
    {
        if (v == null) return "<null>";
        if (v.IsString) return "\"" + v.String + "\"";
        if (v.IsBoolean) return v.Bool ? "true" : "false";
        if (v.IsInt) return v.Int.ToString();
        if (v.IsArray)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (var i = 0; i < v.Count && i < 4; i++)
            {
                var e = v[i];
                parts.Add(e.IsString ? "\"" + e.String + "\"" : e.IsObject ? "{obj}" : e.IsArray ? "[arr]" : "?");
            }

            return "[数组 " + v.Count + "] " + string.Join(", ", parts);
        }

        if (v.IsObject) return "{对象 " + v.Count + " 字段}";
        return "?";
    }

    /// <summary>
    /// 【关键注册步骤】把 mod 新增的 CharacterPerk 挂进游戏的特质页（PerkTabGroup.ContainedPerks）。
    /// 本版本游戏的特质选择界面读的是 PerkTabGroup.ContainedPerks，而不是 PerkGroup.PerksList；
    /// mod 数据里也没有任何字段能把特质塞进这些页签，所以只能在 warp 之后手动登记。
    /// </summary>
    public static void RegisterModPerksIntoTabGroups()
    {
        var tabs = RegistryByClass("PerkTabGroup", 8);
        MelonLogger.Msg("[TAB] 注册表 PerkTabGroup = " + tabs.Count + " ; mod 特质 = " + ModPerks.Count);
        if (tabs.Count == 0 || ModPerks.Count == 0)
        {
            MelonLogger.Warning("[TAB] 跳过登记：页签或特质为空");
            return;
        }

        foreach (var kv in tabs)
        {
            try
            {
                var tab = CastOrNull<PerkTabGroup>(kv.Value);
                if (tab == null)
                {
                    MelonLogger.Warning("[TAB] TryCast 失败 " + kv.Key);
                    continue;
                }

                var list = tab.ContainedPerks;
                if (list == null)
                {
                    MelonLogger.Warning("[TAB] " + NameOf(tab) + " ContainedPerks = null");
                    continue;
                }

                int before = list.Count;
                int added = 0;
                foreach (var p in ModPerks)
                {
                    try
                    {
                        list.Add(p);
                        added++;
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("[TAB] Add 失败: " + e.GetType().Name + " " + e.Message);
                        break;
                    }
                }

                MelonLogger.Msg("[TAB] \"" + NameOf(tab) + "\" IncludesAllPerks=" + tab.IncludesAllPerks
                                + " ContainedPerks " + before + " → " + tab.ContainedPerks.Count
                                + " (加入 " + added + ")");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[TAB] 登记失败 " + kv.Key + ": " + e.GetType().Name + " " + e.Message);
            }
        }
    }

    /// <summary>
    /// 扫游戏自己程序集里所有「引用了 CharacterPerk / PerkTabGroup」的属性，
    /// 用来回答：特质界面到底从哪个集合读特质（PerkTabGroup.ContainedPerks？还是别的 UI 组件字段？）。
    /// </summary>
    public static void ScanTypesReferencingPerks()
    {
        var hits = 0;
        try
        {
            foreach (var t in LoadUtil.LoadArchMod.TypesInGameAssembly())
            {
                if (t == null) continue;
                try
                {
                    var props = t.GetProperties(System.Reflection.BindingFlags.Public |
                                                System.Reflection.BindingFlags.NonPublic |
                                                System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.DeclaredOnly);
                    foreach (var p in props)
                    {
                        var pt = p.PropertyType;
                        string n;
                        if (pt.IsGenericType)
                            n = pt.Name.Split('`')[0] + "<" +
                                string.Join(",", pt.GetGenericArguments().Select(a => a.Name)) + ">";
                        else if (pt.IsArray)
                            n = pt.GetElementType()?.Name + "[]";
                        else
                            n = pt.Name;

                        if (n.IndexOf("CharacterPerk", StringComparison.Ordinal) < 0 &&
                            n.IndexOf("PerkTabGroup", StringComparison.Ordinal) < 0) continue;
                        MelonLogger.Msg("[SCAN] " + t.Name + "." + p.Name + " : " + n);
                        if (++hits >= 80)
                        {
                            MelonLogger.Msg("[SCAN] 达到 80 条上限，停止");
                            return;
                        }
                    }
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }

            MelonLogger.Msg("[SCAN] 引用 CharacterPerk/PerkTabGroup 的属性共 " + hits + " 条");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[SCAN] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>打印 mod 特质在 warp 之后的真实字段值（AddedCards / EquippedCards 等引用数组是否真的写进去了）。</summary>
    public static void DumpModPerksPostWarp()
    {
        MelonLogger.Msg("[MP] mod 特质 = " + ModPerks.Count + " 个");
        var dumpName = 0;
        foreach (var p in ModPerks)
        {
            try
            {
                MelonLogger.Msg("[MP] " + NameOf(p) + " GUID=" + SafeUniqueId(p)
                                + " AddedCards=" + Render(p.AddedCards)
                                + " EquippedCards=" + Render(p.EquippedCards)
                                + " StartUnlocked=" + p.StartUnlocked
                                + " DifficultyRating=" + p.DifficultyRating);
                if (dumpName++ < 2)
                {
                    MelonLogger.Msg("[MP]   显示条件: HiddenUntilUnlocked=" + p.HiddenUntilUnlocked
                                    + " SunsCost=" + p.SunsCost + " MoonsCost=" + p.MoonsCost
                                    + " RequiredDifficultyScore=" + Render(p.RequiredDifficultyScore)
                                    + " PerkUnlockConditions=" + Render(p.PerkUnlockConditions)
                                    + " PerkIcon=" + Render(p.PerkIcon));
                    // 特质名/描述是 LocalizedString：如果本地化没进游戏，界面会显示空白（用户会当成"没生效"）
                    try { Describe(p.PerkName, typeof(LocalizedString), 8); }
                    catch (Exception e) { MelonLogger.Warning("[MP]   PerkName 转储失败: " + e.Message); }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[MP] 转储失败: " + e.GetType().Name + " " + e.Message);
            }
        }
    }

    // ═══════════ JSON ↔ 加载后对象 逐字段对账（通病级判据；只报差异，不设上限） ═══════════
    public static int DiffChecked, DiffMismatch;
    /// <summary>诊断用：mod 卡的作者原始 JSON（key=卡 GUID），供 JSON↔对象 逐字段对账。</summary>
    public static readonly Dictionary<string, KVProvider> ModCardJsonSource = new();
    private static int DiffLines;

    /// <summary>
    /// 把**作者原始 JSON** 与**我们加载出来的对象**逐字段对账，只打印差异：
    ///   `[MODCARD-DIFF] <标签> 字段=<名> JSON=<有/计数/值> 对象=<无/计数/值>`
    /// 递归覆盖嵌套对象、数组元素与内联结构（读操作，值类型也能读）；`*WarpData` 视为"目标字段应非空"。
    /// 任何 mod、任何卡、任何条目都适用 —— 没有名字特判、没有数量上限、无裸内存。
    /// </summary>
    public static void DiffJsonVsObject(object obj, KVProvider json, string label, int depth = 0)
    {
        try
        {
            if (obj == null || json == null || !json.IsObject || depth > 6) return;
            foreach (var k in json.Keys)
            {
                if (k.EndsWith("WarpType")) continue;
                if (k == "m_FileID" || k == "m_PathID") continue;
                var jv = json[k];

                // 引用型：JSON 里有几项 → 目标字段就应该有几项（空 = 漏解引用）
                if (k.EndsWith("WarpData"))
                {
                    var fld = k.Substring(0, k.Length - 8);
                    var want = jv.IsArray ? jv.Count : (jv.IsString ? 1 : 0);
                    if (want <= 0) continue;
                    var got = (int)ElemCount(Member(obj, fld));
                    DiffChecked++;
                    if (got == 0)
                    {
                        DiffMismatch++;
                        Report("字段=" + label + "." + fld + " JSON=" + want + " 项 对象=0 项（引用未写入）");
                    }

                    continue;
                }

                var ov = Member(obj, k);
                if (ov == null)
                {
                    DiffChecked++;
                    DiffMismatch++;
                    Report("字段=" + label + "." + k + " JSON=" + Kind(jv) + " 对象=null（字段缺失）");
                    continue;
                }

                if (jv.IsObject)
                {
                    // Unity 占位对象（只有 m_FileID/m_PathID）不是真结构 → 跳过，避免假差异
                    var ph = true;
                    foreach (var kk in jv.Keys)
                        if (kk != "m_FileID" && kk != "m_PathID") { ph = false; break; }
                    if (ph) continue;
                    DiffJsonVsObject(ov, jv, label + "." + k, depth + 1);
                    continue;
                }

                if (jv.IsArray)
                {
                    var jn = jv.Count;
                    var on = (int)ElemCount(ov);
                    DiffChecked++;
                    if (jn != on) { DiffMismatch++; Report("字段=" + label + "." + k + " JSON=" + jn + " 项 对象=" + on + " 项"); }
                    for (var i = 0; i < jn && i < on && i < 3; i++)
                        if (jv[i].IsObject) DiffJsonVsObject(GetElem(ov, i), jv[i], label + "." + k + "[" + i + "]", depth + 1);
                    continue;
                }

                // 标量：宽松比较（int/bool/字符串），类型对不上就不算差异
                var js = jv.ToString().Trim().Trim('"');
                var os = DescribeValue(ov).Trim();
                DiffChecked++;
                if (js.Length > 0 && os.Length > 0 && js != os
                    && !(js == "0" && os == "0") && !os.Contains(js))
                {
                    DiffMismatch++;
                    Report("字段=" + label + "." + k + " JSON=" + js + " 对象=" + os);
                }
            }
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    private static string Kind(KVProvider v)
    {
        if (v == null) return "?";
        if (v.IsObject) return "对象";
        if (v.IsArray) return v.Count + " 项数组";
        return v.ToString();
    }

    private static void Report(string line)
    {
        DiffLines++;
        MelonLogger.Msg("[MODCARD-DIFF] " + line);
    }
    // ═══════════ 本地化判据（★ 按来源，不按 mod 名字；对任何 mod 都成立） ═══════════
    private static readonly HashSet<string> ModLocalizationKeys = new();

    /// <summary>登记一条"来自 mod 包"的本地化键（由 LoadPatchMain 的 CSV 装载处调用，与 mod 名字无关）。</summary>
    public static void NoteModLocalizationKey(string key)
    {
        try
        {
            if (!string.IsNullOrEmpty(key) && ModLocalizationKeys.Count < 20000) ModLocalizationKeys.Add(key);
        }
        catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>
    /// `[L10N] 游戏文本总数=… mod 注入键=K 已进游戏=H/K ✓/⚠` —— 判据只看"**我们注入的**键有多少真的
    /// 出现在 `LocalizationManager.CurrentTexts` 里"，**不依赖任何 mod 名/卡名**（任何 mod 都适用）。
    /// </summary>
    public static void DumpLocalizationModKeys()
    {
        try
        {
            var texts = LocalizationManager.CurrentTexts;
            if (texts == null)
            {
                MelonLogger.Warning("[L10N] CurrentTexts = null");
                return;
            }

            int total = texts.Count, hit = 0;
            var miss = new List<string>();
            foreach (var k in ModLocalizationKeys)
            {
                if (texts.ContainsKey(k)) hit++;
                else if (miss.Count < 6) miss.Add(k);
            }

            var verdict = ModLocalizationKeys.Count == 0
                ? "（本包没有本地化块）"
                : hit == ModLocalizationKeys.Count
                    ? " ✓ 全部进游戏"
                    : " ⚠ 有缺失";
            MelonLogger.Msg("[L10N] 游戏文本总数=" + total + " mod 注入键=" + ModLocalizationKeys.Count
                            + " 已进游戏=" + hit + "/" + ModLocalizationKeys.Count + verdict
                            + (miss.Count > 0 ? " 缺失抽样=[" + string.Join(", ", miss) + "]" : ""));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[L10N] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>按 il2cpp 真实类名扫描游戏注册表（纯托管，无 ICall）。</summary>
    public static List<KeyValuePair<string, UniqueIDScriptable>> RegistryByClass(string clsName, int max)
    {
        var list = new List<KeyValuePair<string, UniqueIDScriptable>>();
        try
        {
            var dict = UniqueIDScriptable.AllUniqueObjects;
            if (dict == null) return list;
            foreach (var kv in dict)
            {
                try
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (Cls(v) != clsName) continue;
                    list.Add(new KeyValuePair<string, UniqueIDScriptable>(kv.Key, v));
                    if (max > 0 && list.Count >= max) break;
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DIAG] RegistryByClass(" + clsName + ") 失败: " + e.GetType().Name + " " + e.Message);
        }

        return list;
    }

    /// <summary>统计注册表里各真实类名的条目数（只统计前 maxDistinct 个类）。</summary>
    public static void DumpRegistryClassHistogram(int top)
    {
        try
        {
            var dict = UniqueIDScriptable.AllUniqueObjects;
            if (dict == null)
            {
                MelonLogger.Warning("[DIAG] AllUniqueObjects = null");
                return;
            }

            var hist = new Dictionary<string, int>();
            int total = 0;
            foreach (var kv in dict)
            {
                try
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    var c = Cls(v);
                    hist.TryGetValue(c, out var n);
                    hist[c] = n + 1;
                    total++;
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }

            MelonLogger.Msg("[DIAG] 注册表真实类名直方图（总 " + total + " 条，前 " + top + " 个类）:");
            foreach (var kv in hist.OrderByDescending(p => p.Value))
                MelonLogger.Msg("[DIAG]   " + kv.Key + " = " + kv.Value);
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DIAG] 直方图失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// 探针：本机能不能用 Resources.FindObjectsOfTypeAll / Object.FindObjectsOfType 枚举全部 ScriptableObject。
    /// 只在 ICall 真解析出地址时才调用托管 API（否则会走缺失 icall 分支刷屏/异常）。
    /// </summary>
    public static void ProbeEnumerationApis()
    {
        IntPtr pAll = IntPtr.Zero, pMany = IntPtr.Zero, pOne = IntPtr.Zero;
        try
        {
            pAll = RawTexture.ResolveIcall("UnityEngine.Resources::FindObjectsOfTypeAll");
            pMany = RawTexture.ResolveIcall("UnityEngine.Object::FindObjectsOfType");
            pOne = RawTexture.ResolveIcall("UnityEngine.Object::FindObjectOfType");
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[PROBE] ResolveIcall 失败: " + e.GetType().Name + " " + e.Message);
        }

        MelonLogger.Msg("[PROBE] ICall 地址: Resources::FindObjectsOfTypeAll=0x" + pAll.ToInt64().ToString("X")
                        + " Object::FindObjectsOfType=0x" + pMany.ToInt64().ToString("X")
                        + " Object::FindObjectOfType=0x" + pOne.ToInt64().ToString("X"));

        // [FIX] 原来的守卫是 `pAll != 0`（FindObjectsOfTypeAll 真机 MISS → 永远 0 → 这段是死代码）；
        // 现在守卫改成可用的 `Object::FindObjectsOfType`。
        if (pMany != IntPtr.Zero)
        {
            try
            {
                // [2026-10-02 修正] 真机实测 `Resources::FindObjectsOfTypeAll` **MISS**（必失败），
                // 而 `Object::FindObjectsOfType(Type)` 是 HAVE → 这里改用后者（同一个语义：按类型枚举全部对象）。
                var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ScriptableObject>());
                int n = 0, perk = 0, card = 0, group = 0;
                string firstPg = null;
                if (all != null)
                    foreach (var o in all)
                    {
                        n++;
                        var c = Cls(o);
                        if (c == "PerkGroup") { perk++; firstPg ??= NameOf(o); }
                        else if (c == "CharacterPerk") card++;
                        else if (c == "CardData") group++;
                    }

                MelonLogger.Msg("[PROBE] Object.FindObjectsOfType(ScriptableObject) 返回 " + n
                                + " ; PerkGroup=" + perk + " CharacterPerk=" + card + " CardData=" + group
                                + " 首个PerkGroup名字=" + (firstPg ?? "<无>"));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[PROBE] FindObjectsOfType 调用失败: " + e.GetType().Name + " " + e.Message);
            }
        }
        else
        {
            MelonLogger.Msg("[PROBE] 跳过按类型枚举（Object::FindObjectsOfType 不可用）");
        }
    }

    /// <summary>
    /// 用真实类名把游戏自带的 PerkGroup 全部捞出来，建 name → PerkGroup 索引。
    /// 别名规则：主名 = obj.name；另加「去掉第一个下划线前缀」（mod 对象会被重命名成 modName_Xxx）。
    /// </summary>
    public static Dictionary<string, PerkGroup> BuildPerkGroupIndex(out int scanned, out int matched)
    {
        var idx = new Dictionary<string, PerkGroup>();
        scanned = 0;
        matched = 0;
        try
        {
            var dict = UniqueIDScriptable.AllUniqueObjects;
            if (dict == null) return idx;
            foreach (var kv in dict)
            {
                scanned++;
                try
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (Cls(v) != "PerkGroup") continue;
                    matched++;
                    PerkGroup pg;
                    try { pg = v.TryCast<PerkGroup>(); }
                    catch { pg = null; }
                    if (pg == null) continue;

                    string nm = null;
                    try { nm = pg.name; } catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                    if (!string.IsNullOrEmpty(nm) && !idx.ContainsKey(nm)) idx[nm] = pg;
                    if (!string.IsNullOrEmpty(nm))
                    {
                        var us = nm.IndexOf('_');
                        if (us > 0 && us + 1 < nm.Length)
                        {
                            var alias = nm.Substring(us + 1);
                            if (!string.IsNullOrEmpty(alias) && !idx.ContainsKey(alias)) idx[alias] = pg;
                        }
                    }

                    if (!string.IsNullOrEmpty(kv.Key) && !idx.ContainsKey(kv.Key)) idx[kv.Key] = pg;
                }
                catch (Exception __e) { MelonLogger.Warning("[Diag] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DIAG] BuildPerkGroupIndex 失败: " + e.GetType().Name + " " + e.Message);
        }

        return idx;
    }
}
