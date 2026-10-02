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
        catch
        {
        }
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
        catch
        {
        }

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
                try { tabs = menu.AllPerkTabs?.Count ?? -1; } catch { }
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
        catch
        {
        }

        return false;
    }

    /// <summary>取 interop 代理类型对应的原生 Il2CppClass*。</summary>
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
        catch
        {
        }

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
        long sumBefore = 0, sumAfter = 0;
        var samples = new System.Collections.Generic.List<string>();
        foreach (var kv in before)
        {
            sumBefore += kv.Value.Size;
            var parts = kv.Key.Split('|');
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
            catch
            {
            }

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
        catch
        {
        }
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
            catch { }
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
            catch
            {
            }
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
            catch { }
            if (!isList && !IsIl2CppArrayType(ft)) continue;

            try
            {
                var v = WarpperClassGen.MainGenTools.CommonGet(o, kv.Key);
                sum += ElemCount(v);
            }
            catch
            {
            }
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
                        catch
                        {
                        }

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
        catch
        {
        }

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
        catch
        {
        }

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
        catch
        {
        }
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
        catch
        {
        }
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
        catch
        {
        }
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
        catch
        {
        }
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
        catch
        {
        }

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
                catch
                {
                }

                if (found != null) break;
            }
        }
        catch
        {
        }

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
        catch
        {
        }
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
                catch
                {
                }

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
            foreach (var k in json.Keys.Take(max))
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
                catch
                {
                }
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

    /// <summary>检查 mod 的本地化文本有没有真的进游戏（特质名/描述为空会让用户以为没生效）。</summary>
    public static void DumpLocalizationWindyKeys()
    {
        try
        {
            var texts = LocalizationManager.CurrentTexts;
            if (texts == null)
            {
                MelonLogger.Warning("[L10N] CurrentTexts = null");
                return;
            }

            int total = 0, windy = 0;
            var samples = new System.Collections.Generic.List<string>();
            foreach (var kv in texts)
            {
                total++;
                var k = kv.Key;
                if (k == null) continue;
                if (k.IndexOf("Windy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    k.IndexOf("windy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    windy++;
                    if (samples.Count < 6) samples.Add(k);
                }
            }

            MelonLogger.Msg("[L10N] 文本总数=" + total + " 含 Windy 的键=" + windy
                            + " 抽样=[" + string.Join(", ", samples) + "]");
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
                catch
                {
                }
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
                catch
                {
                }
            }

            MelonLogger.Msg("[DIAG] 注册表真实类名直方图（总 " + total + " 条，前 " + top + " 个类）:");
            foreach (var kv in hist.OrderByDescending(p => p.Value).Take(top))
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
                    try { nm = pg.name; } catch { }
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
                catch
                {
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[DIAG] BuildPerkGroupIndex 失败: " + e.GetType().Name + " " + e.Message);
        }

        return idx;
    }
}
