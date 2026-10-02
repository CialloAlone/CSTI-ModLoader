using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

/// <summary>
/// 纯原生（全程 IntPtr）的 UnityEngine 纹理 / 精灵工具。
///
/// 为什么必须这么写：
///   托管 <c>UnityEngine.Texture2D</c> 类型在本作（CSTI + Il2CppInterop 1.5.3 + .NET 8）上是致命的——
///   任何一次类型解析（<c>Il2CppType.Of&lt;Texture2D&gt;()</c> / <c>typeof(Texture2D)</c> / <c>new Texture2D(...)</c>）
///   都会走到 mono_class_from_mono_type_internal 的未实现分支，进程直接 SIGABRT。
///   本类不出现任何托管 Unity 类型，全部通过 <see cref="IL2CPP"/> 的原生导出函数操作，对象一律用 IntPtr 传递。
///
/// 已在本机 interop_out 生成代码中核对过的事实（写代码时依赖这些结论）：
///   1) <c>il2cpp_runtime_invoke</c> 对<b>值类型返回值</b>返回的是<b>装箱对象</b>指针，
///      必须 <c>il2cpp_object_unbox(res)</c> 之后再读（与 Il2CppInterop 生成代码完全一致）。
///   2) 原生 <c>UnityEngine.ImageConversion.LoadImage</c> 的参数是 (Texture2D, byte[], bool) —— <b>3 个</b>。
///      2 参数版本只是 Il2CppInterop 为默认参数生成的托管包装，原生层取不到。
///   3) <c>il2cpp_array_new(elementClass, length)</c> 用的是<b>元素类</b>指针（不是 Il2CppType*，也不是数组类）；
///      Il2CppArray 的数据区偏移 = 4 * IntPtr.Size（与 Il2CppInterop 的 Il2CppStructArray&lt;T&gt;.ArrayStartPointer 相同）。
///   4) <c>Sprite.Create</c> 有多个同参数个数的重载（还有 Create(Rect,Vector2,float)），
///      按名字+参数个数取会取错，必须再校验第一个参数类型是不是 Texture2D。
///   5) 原生方法都是静态方法，<c>il2cpp_runtime_invoke</c> 的 obj 必须传 IntPtr.Zero。
/// </summary>
public static class RawTexture
{
    // ---------------------------------------------------------------- 常量

    /// <summary>引擎程序集名必须带 .dll 后缀，否则 IL2CPP.GetIl2CppClass 返回 0。</summary>
    public const string CoreModule = "UnityEngine.CoreModule.dll";
    public const string ImageConversionModule = "UnityEngine.ImageConversionModule.dll";
    public const string MscorlibModule = "Il2Cppmscorlib.dll";

    /// <summary>UnityEngine.TextureFormat.RGBA32 == 4</summary>
    public const int TextureFormatRGBA32 = 4;

    // ---------------------------------------------------------------- 诊断开关

    /// <summary>可选日志回调；为 null 时不输出。测试 mod 会接到 MelonLoader 的 LoggerInstance。</summary>
    public static Action<string> Log;

    /// <summary>
    /// 是否对创建出来的原生对象加一个强 GC 句柄（il2cpp_gchandle_new）。
    /// 我们只持有裸 IntPtr，IL2CPP 的保守 GC 有可能回收它，加句柄可根化对象。
    /// 关闭它不会影响 API 形状，只影响生命周期安全。
    /// </summary>
    public static bool PinCreatedObjects = true;

    /// <summary>最近一次失败原因；成功时为 null。</summary>
    public static string LastError { get; private set; }

    static readonly List<IntPtr> RootHandles = new List<IntPtr>();

    // ---------------------------------------------------------------- 缓存

    static IntPtr _clsTexture2D, _clsImageConversion, _clsSprite, _clsByte;
    static IntPtr _ctorTexture2D4, _ctorTexture2D2;
    static IntPtr _mLoadImage, _mSpriteCreate;

    static void L(string msg)
    {
        try { Log?.Invoke(msg); } catch { /* 日志失败绝不能影响主流程 */ }
    }

    // ================================================================
    //  公共 API（签名固定，MiniLoader 移植按此编码）
    // ================================================================

    /// <summary>
    /// 创建一个 4x4 RGBA32、无 mipmap 的 Texture2D，返回原生对象指针。
    /// 等价于 <c>new Texture2D(w, h, TextureFormat.RGBA32, false)</c>，但全程不触碰托管 Texture2D 类型。
    /// </summary>
    public static IntPtr CreateTexture2D(int w, int h)
        => CreateTexture2D(w, h, TextureFormatRGBA32, false);

    /// <summary>完整参数的创建入口（format 为 UnityEngine.TextureFormat 的整数值）。</summary>
    public static IntPtr CreateTexture2D(int w, int h, int format, bool mipChain)
    {
        LastError = null;
        if (w <= 0 || h <= 0) { LastError = "尺寸非法: " + w + "x" + h; return IntPtr.Zero; }

        var cls = GetTexture2DClass();
        if (cls == IntPtr.Zero) { LastError = "找不到 Texture2D 类（" + CoreModule + "）"; return IntPtr.Zero; }

        EnsureTextureCtors(cls);
        if (_ctorTexture2D4 == IntPtr.Zero && _ctorTexture2D2 == IntPtr.Zero)
        {
            LastError = "找不到 Texture2D 构造函数（.ctor/4 与 .ctor/2 均为 0）";
            return IntPtr.Zero;
        }

        var obj = IL2CPP.il2cpp_object_new(cls);
        if (obj == IntPtr.Zero) { LastError = "il2cpp_object_new 返回 0"; return IntPtr.Zero; }

        unsafe
        {
            int width = w, height = h, fmt = format;
            bool mip = mipChain;
            IntPtr exc = IntPtr.Zero;

            if (_ctorTexture2D4 != IntPtr.Zero)
            {
                var args = stackalloc IntPtr[4];
                args[0] = (IntPtr)(&width);
                args[1] = (IntPtr)(&height);
                args[2] = (IntPtr)(&fmt);
                args[3] = (IntPtr)(&mip);
                _ = IL2CPP.il2cpp_runtime_invoke(_ctorTexture2D4, obj, (void**)args, ref exc);
            }
            else
            {
                // 兜底：只有 .ctor(int,int) 时用它（默认 RGBA32 + 无 mipmap）
                L("CreateTexture2D: .ctor/4 缺失，退化使用 .ctor/2");
                var args = stackalloc IntPtr[2];
                args[0] = (IntPtr)(&width);
                args[1] = (IntPtr)(&height);
                _ = IL2CPP.il2cpp_runtime_invoke(_ctorTexture2D2, obj, (void**)args, ref exc);
            }

            if (exc != IntPtr.Zero)
            {
                LastError = "Texture2D..ctor 抛异常: " + DescribeException(exc);
                return IntPtr.Zero;
            }
        }

        Root(obj);
        L("CreateTexture2D(" + w + ", " + h + ", fmt=" + format + ", mip=" + mipChain + ") -> 0x" + Hex(obj));
        return obj;
    }

    /// <summary>
    /// 把图片字节（PNG/JPG）加载进纹理。内部自行构造 IL2CPP byte[]，再原生调用
    /// UnityEngine.ImageConversion.LoadImage(Texture2D, byte[], bool markNonReadable=false)。
    /// 返回原生方法的 bool 结果（失败时 <see cref="LastError"/> 有原因）。
    /// </summary>
    /// <summary>解析一条 IL2CPP ICall 的本地函数指针（0 = 未注册）。</summary>
    public static IntPtr ResolveIcall(string name)
    {
        try { return IL2CPP.il2cpp_resolve_icall(name); }
        catch { return IntPtr.Zero; }
    }

    public static bool LoadImage(IntPtr tex, byte[] data)
    {
        LastError = null;
        if (tex == IntPtr.Zero) { LastError = "纹理指针为 0"; return false; }
        if (data == null || data.Length == 0) { LastError = "图片数据为空"; return false; }

        var cls = GetImageConversionClass();
        if (cls == IntPtr.Zero) { LastError = "找不到 ImageConversion 类（" + ImageConversionModule + "）"; return false; }

        var method = GetLoadImageMethod(cls);
        if (method == IntPtr.Zero) { LastError = "找不到 ImageConversion.LoadImage"; return false; }

        int pc = (int)IL2CPP.il2cpp_method_get_param_count(method);

        string arrErr;
        L("  L1 准备构造 IL2CPP byte[]，长度=" + data.Length);
        var arr = NewIl2CppByteArray(data, out arrErr);
        if (arr == IntPtr.Zero) { LastError = "构造 IL2CPP byte[] 失败: " + arrErr; return false; }
        L("  L2 byte[] 构造成功 ptr=0x" + arr.ToString("X") + " 参数个数=" + pc);

        bool result;
        unsafe
        {
            IntPtr texLocal = tex;
            IntPtr arrLocal = arr;
            bool markNonReadable = false;

            var args = stackalloc IntPtr[pc];
            args[0] = (IntPtr)(&texLocal);
            if (pc > 1) args[1] = (IntPtr)(&arrLocal);
            if (pc > 2) args[2] = (IntPtr)(&markNonReadable);

            IntPtr exc = IntPtr.Zero;
            L("  L3 即将 il2cpp_runtime_invoke(LoadImage) tex=0x" + tex.ToString("X"));
            var res = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, (void**)args, ref exc);
            L("  L4 invoke 返回 res=0x" + res.ToString("X") + " exc=0x" + exc.ToString("X"));
            if (exc != IntPtr.Zero)
            {
                LastError = "LoadImage 抛异常: " + DescribeException(exc);
                result = false;
            }
            else if (res == IntPtr.Zero)
            {
                LastError = "LoadImage 返回 null（bool 未装箱？）";
                result = false;
            }
            else
            {
                // 值类型返回值是装箱对象：必须 unbox 之后再取值
                var unboxed = IL2CPP.il2cpp_object_unbox(res);
                result = unboxed != IntPtr.Zero && *(byte*)unboxed != 0;
                if (!result) LastError = "LoadImage 返回 false（Unity 认为这不是合法图片数据）";
            }
        }

        L("LoadImage(tex=0x" + Hex(tex) + ", bytes=" + data.Length + ", 方法=" + DescribeMethod(method)
          + ") -> " + (result ? "true" : "false") + (result ? "" : "  [" + LastError + "]"));
        return result;
    }

    /// <summary>
    /// 用纹理的 (x, y, w, h) 像素区域建一个 Sprite（pivot 取 0.5,0.5），返回原生 Sprite 指针。
    /// 等价于 <c>Sprite.Create(tex, new Rect(x, y, w, h), new Vector2(0.5f, 0.5f))</c>。
    /// </summary>
    public static IntPtr CreateSprite(IntPtr tex, float x, float y, float w, float h)
    {
        LastError = null;
        if (tex == IntPtr.Zero) { LastError = "纹理指针为 0"; return IntPtr.Zero; }

        var cls = GetSpriteClass();
        if (cls == IntPtr.Zero) { LastError = "找不到 Sprite 类（" + CoreModule + "）"; return IntPtr.Zero; }

        var method = GetSpriteCreateMethod(cls);
        if (method == IntPtr.Zero) { LastError = "找不到 Sprite.Create(Texture2D, Rect, Vector2)"; return IntPtr.Zero; }

        int pc = (int)IL2CPP.il2cpp_method_get_param_count(method);

        unsafe
        {
            IntPtr texLocal = tex;

            // Rect 结构布局就是 4 个连续 float：m_XMin, m_YMin, m_Width, m_Height
            float* rect = stackalloc float[4];
            rect[0] = x; rect[1] = y; rect[2] = w; rect[3] = h;

            // Vector2：x, y
            float* pivot = stackalloc float[2];
            pivot[0] = 0.5f; pivot[1] = 0.5f;

            // 以下是更长重载的默认值（只用到哪个填哪个）
            float pixelsPerUnit = 100f;
            uint extrude = 0;
            int meshType = 0;              // SpriteMeshType.FullRect
            float* border = stackalloc float[4];
            border[0] = 0; border[1] = 0; border[2] = 0; border[3] = 0;
            bool fallbackShape = false;

            var args = stackalloc IntPtr[pc];
            args[0] = (IntPtr)(&texLocal);
            if (pc > 1) args[1] = (IntPtr)rect;
            if (pc > 2) args[2] = (IntPtr)pivot;
            if (pc > 3) args[3] = (IntPtr)(&pixelsPerUnit);
            if (pc > 4) args[4] = (IntPtr)(&extrude);
            if (pc > 5) args[5] = (IntPtr)(&meshType);
            if (pc > 6) args[6] = (IntPtr)border;
            if (pc > 7) args[7] = (IntPtr)(&fallbackShape);

            IntPtr exc = IntPtr.Zero;
            var sprite = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, (void**)args, ref exc);
            if (exc != IntPtr.Zero)
            {
                LastError = "Sprite.Create 抛异常: " + DescribeException(exc);
                return IntPtr.Zero;
            }
            if (sprite == IntPtr.Zero)
            {
                LastError = "Sprite.Create 返回 0";
                return IntPtr.Zero;
            }

            Root(sprite);
            L("CreateSprite(tex=0x" + Hex(tex) + ", rect=(" + x + "," + y + "," + w + "," + h + "), 方法="
              + DescribeMethod(method) + ") -> 0x" + Hex(sprite));
            return sprite;
        }
    }

    /// <summary>
    /// 把原生指针包装成托管 interop 对象（如 "UnityEngine.Sprite, UnityEngine.CoreModule"）。
    /// 严禁用于 Texture2D —— 托管 Texture2D 类型解析会 SIGABRT，这里直接拒绝。
    /// </summary>
    public static object Wrap(IntPtr ptr, string assembly, string ns, string type)
    {
        LastError = null;
        if (ptr == IntPtr.Zero) return null;
        if (string.IsNullOrEmpty(type)) { LastError = "类型名为空"; return null; }
        if (string.Equals(type, "Texture2D", StringComparison.Ordinal))
        {
            LastError = "拒绝包装 Texture2D：托管类型解析会触发 Mono 缺陷（SIGABRT）";
            L("Wrap 拒绝: " + LastError);
            return null;
        }

        try
        {
            string full = (string.IsNullOrEmpty(ns) ? type : ns + "." + type) + ", " + assembly;
            var t = Type.GetType(full, throwOnError: false);
            if (t == null) { LastError = "找不到托管类型: " + full; return null; }
            if (t.FullName == "UnityEngine.Texture2D") { LastError = "拒绝包装 Texture2D"; return null; }
            return Activator.CreateInstance(t, new object[] { ptr });
        }
        catch (Exception e)
        {
            LastError = "Wrap 失败: " + e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    // ================================================================
    //  诊断辅助（附加 API，不影响上面的固定签名）
    // ================================================================

    /// <summary>把 IL2CPP 异常指针格式化成可读文本。</summary>
    public static string DescribeException(IntPtr exc)
    {
        if (exc == IntPtr.Zero) return "<null>";
        unsafe
        {
            const int Size = 1024;
            byte* buf = stackalloc byte[Size];
            buf[0] = 0;
            IL2CPP.il2cpp_format_exception(exc, buf, Size - 1);
            var s = Marshal.PtrToStringAnsi((IntPtr)buf);
            return string.IsNullOrEmpty(s) ? ("0x" + Hex(exc)) : s;
        }
    }

    /// <summary>打印一个 MethodInfo 的签名（名称 + 参数类型），用于确认真机上取到的是哪个重载。</summary>
    public static string DescribeMethod(IntPtr method)
    {
        if (method == IntPtr.Zero) return "<null>";
        unsafe
        {
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)) ?? "?";
            int pc = (int)IL2CPP.il2cpp_method_get_param_count(method);
            var sb = new System.Text.StringBuilder();
            sb.Append(name).Append('(');
            for (int i = 0; i < pc; i++)
            {
                if (i > 0) sb.Append(", ");
                var pt = IL2CPP.il2cpp_method_get_param(method, (uint)i);
                sb.Append(pt == IntPtr.Zero ? "?" : (Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(pt)) ?? "?"));
            }
            return sb.Append(')').ToString();
        }
    }

    /// <summary>一键解析所有需要的类/方法并返回多行报告，便于真机日志确认。</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var tex = GetTexture2DClass();
            EnsureTextureCtors(tex);
            var img = GetImageConversionClass();
            GetLoadImageMethod(img);       // 触发解析，保证下面打印的是真实指针
            var spr = GetSpriteClass();
            GetSpriteCreateMethod(spr);
            var byt = GetByteClass();

            sb.AppendLine("Texture2D 类        = 0x" + Hex(tex) + "  (" + CoreModule + ")");
            sb.AppendLine("  .ctor/4           = 0x" + Hex(_ctorTexture2D4) + (tex != IntPtr.Zero ? "  " + DescribeMethod(_ctorTexture2D4) : ""));
            sb.AppendLine("  .ctor/2           = 0x" + Hex(_ctorTexture2D2) + (tex != IntPtr.Zero ? "  " + DescribeMethod(_ctorTexture2D2) : ""));
            sb.AppendLine("ImageConversion 类  = 0x" + Hex(img) + "  (" + ImageConversionModule + ")");
            sb.AppendLine("  LoadImage         = 0x" + Hex(_mLoadImage) + (img != IntPtr.Zero ? "  " + DescribeMethod(_mLoadImage) : ""));
            sb.AppendLine("Sprite 类           = 0x" + Hex(spr) + "  (" + CoreModule + ")");
            sb.AppendLine("  Create            = 0x" + Hex(_mSpriteCreate) + (spr != IntPtr.Zero ? "  " + DescribeMethod(_mSpriteCreate) : ""));
            sb.AppendLine("System.Byte 类      = 0x" + Hex(byt) + "  (" + MscorlibModule + ")");
            sb.Append("数组数据偏移        = " + (4 * IntPtr.Size) + " 字节 (4 * IntPtr.Size)");
        }
        catch (Exception e)
        {
            sb.Append("Describe 失败: ").Append(e);
        }
        return sb.ToString();
    }

    /// <summary>读 IL2CPP 数组的长度（元素个数）。</summary>
    public static uint ArrayLength(IntPtr arr) => arr == IntPtr.Zero ? 0u : IL2CPP.il2cpp_array_length(arr);

    /// <summary>取 IL2CPP 数组的数据区起始指针（偏移 4 * IntPtr.Size）。</summary>
    public static IntPtr ArrayData(IntPtr arr)
    {
        if (arr == IntPtr.Zero) return IntPtr.Zero;
        unsafe { return (IntPtr)((byte*)arr + 4 * IntPtr.Size); }
    }

    /// <summary>在原生对象上加一个强 GC 句柄，避免只持裸指针时被 GC 回收。</summary>
    public static void Root(IntPtr obj)
    {
        if (!PinCreatedObjects || obj == IntPtr.Zero) return;
        try
        {
            var handle = IL2CPP.il2cpp_gchandle_new(obj, false);
            if (handle != IntPtr.Zero) RootHandles.Add(handle);
        }
        catch (Exception e)
        {
            L("Root(0x" + Hex(obj) + ") 失败（忽略）: " + e.Message);
        }
    }

    // ================================================================
    //  内部实现
    // ================================================================

    static string Hex(IntPtr p) => p.ToInt64().ToString("X");

    static IntPtr GetTexture2DClass()
    {
        if (_clsTexture2D == IntPtr.Zero) _clsTexture2D = ResolveClass(CoreModule, "UnityEngine", "Texture2D");
        return _clsTexture2D;
    }

    static IntPtr GetImageConversionClass()
    {
        if (_clsImageConversion == IntPtr.Zero) _clsImageConversion = ResolveClass(ImageConversionModule, "UnityEngine", "ImageConversion");
        return _clsImageConversion;
    }

    static IntPtr GetSpriteClass()
    {
        if (_clsSprite == IntPtr.Zero) _clsSprite = ResolveClass(CoreModule, "UnityEngine", "Sprite");
        return _clsSprite;
    }

    static IntPtr GetByteClass()
    {
        if (_clsByte == IntPtr.Zero) _clsByte = ResolveClass(MscorlibModule, "System", "Byte");
        return _clsByte;
    }

    static void EnsureTextureCtors(IntPtr cls)
    {
        if (cls == IntPtr.Zero) return;
        if (_ctorTexture2D4 == IntPtr.Zero) _ctorTexture2D4 = IL2CPP.il2cpp_class_get_method_from_name(cls, ".ctor", 4);
        if (_ctorTexture2D2 == IntPtr.Zero) _ctorTexture2D2 = IL2CPP.il2cpp_class_get_method_from_name(cls, ".ctor", 2);
    }

    static IntPtr GetLoadImageMethod(IntPtr cls)
    {
        if (_mLoadImage != IntPtr.Zero || cls == IntPtr.Zero) return _mLoadImage;
        // 原生签名是 3 个参数 (Texture2D, byte[], bool)；2 参数的是托管包装，取不到时再兜底试一次
        _mLoadImage = IL2CPP.il2cpp_class_get_method_from_name(cls, "LoadImage", 3);
        if (_mLoadImage == IntPtr.Zero) _mLoadImage = IL2CPP.il2cpp_class_get_method_from_name(cls, "LoadImage", 2);
        return _mLoadImage;
    }

    static IntPtr GetSpriteCreateMethod(IntPtr cls)
    {
        if (_mSpriteCreate != IntPtr.Zero || cls == IntPtr.Zero) return _mSpriteCreate;
        // 3 参数里同时存在 Create(Rect,Vector2,float)，必须校验第一个参数是 Texture2D
        _mSpriteCreate = FindMethodByFirstParam(cls, "Create", 3, "Texture2D");
        if (_mSpriteCreate == IntPtr.Zero)
        {
            L("Sprite.Create/3(Texture2D,...) 未找到，退化为 Create/8");
            _mSpriteCreate = IL2CPP.il2cpp_class_get_method_from_name(cls, "Create", 8);
        }
        return _mSpriteCreate;
    }

    /// <summary>按 名称 + 参数个数 + 第一个参数类型包含指定子串 精确挑方法。</summary>
    static IntPtr FindMethodByFirstParam(IntPtr cls, string name, int paramCount, string firstParamTypeContains)
    {
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        unsafe
        {
            IntPtr iter = IntPtr.Zero;
            IntPtr m;
            while ((m = IL2CPP.il2cpp_class_get_methods(cls, ref iter)) != IntPtr.Zero)
            {
                var mn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(m));
                if (mn != name) continue;
                if ((int)IL2CPP.il2cpp_method_get_param_count(m) != paramCount) continue;
                var p0 = IL2CPP.il2cpp_method_get_param(m, 0);
                var tn = p0 == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(p0));
                if (tn != null && tn.IndexOf(firstParamTypeContains, StringComparison.Ordinal) >= 0) return m;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>构造 IL2CPP System.Byte[] 并写入托管字节，返回数组对象指针。</summary>
    static IntPtr NewIl2CppByteArray(byte[] data, out string err)
    {
        err = null;
        var byteCls = GetByteClass();
        if (byteCls == IntPtr.Zero) { err = "找不到 System.Byte 类"; return IntPtr.Zero; }

        var arr = IL2CPP.il2cpp_array_new(byteCls, (ulong)data.Length);
        if (arr == IntPtr.Zero)
        {
            // 兜底：先取数组类，再用 il2cpp_array_new_specific
            var arrayCls = IL2CPP.il2cpp_array_class_get(byteCls, 1);
            if (arrayCls != IntPtr.Zero) arr = IL2CPP.il2cpp_array_new_specific(arrayCls, (ulong)data.Length);
        }
        if (arr == IntPtr.Zero) { err = "il2cpp_array_new 返回 0"; return IntPtr.Zero; }

        unsafe
        {
            byte* dst = (byte*)arr + 4 * IntPtr.Size;
            Marshal.Copy(data, 0, (IntPtr)dst, data.Length);
        }

        uint len = IL2CPP.il2cpp_array_length(arr);
        uint byteLen = IL2CPP.il2cpp_array_get_byte_length(arr);
        if (len != (uint)data.Length || byteLen != (uint)data.Length)
        {
            err = "IL2CPP byte[] 自检失败: length=" + len + " byteLength=" + byteLen + " 期望=" + data.Length;
            return IntPtr.Zero;
        }
        return arr;
    }

    /// <summary>按 ".dll" 后缀 / 无后缀 / 枚举 domain image 三种方式解析 IL2CPP 类。</summary>
    static IntPtr ResolveClass(string assembly, string ns, string type)
    {
        var cls = IL2CPP.GetIl2CppClass(assembly, ns, type);
        if (cls != IntPtr.Zero) return cls;

        string alt = assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? assembly.Substring(0, assembly.Length - 4)
            : assembly + ".dll";
        cls = IL2CPP.GetIl2CppClass(alt, ns, type);
        if (cls != IntPtr.Zero) return cls;

        // 兜底：自己枚举 domain 里的 image，不依赖 Il2CppInterop 的 ourImagesMap 注册表
        unsafe
        {
            var domain = IL2CPP.il2cpp_domain_get();
            uint count = 0;
            var asms = IL2CPP.il2cpp_domain_get_assemblies(domain, ref count);
            if (asms != null)
            {
                for (uint i = 0; i < count; i++)
                {
                    var img = IL2CPP.il2cpp_assembly_get_image(*(asms + i));
                    if (img == IntPtr.Zero) continue;
                    var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_image_get_name(img));
                    if (!ImageNameMatches(name, assembly)) continue;
                    cls = IL2CPP.il2cpp_class_from_name(img, ns, type);
                    if (cls != IntPtr.Zero)
                    {
                        L("ResolveClass(" + assembly + ", " + ns + "." + type + ") 经 image 枚举命中: " + name);
                        return cls;
                    }
                }

                // 最后一层兜底：完全忽略程序集名，在**所有** image 里找这个全名类。
                // 真机上 mscorlib 的 image 名并不叫 "Il2Cppmscorlib.dll"（System.Byte 就是这样找不到的）。
                for (uint i = 0; i < count; i++)
                {
                    var img = IL2CPP.il2cpp_assembly_get_image(*(asms + i));
                    if (img == IntPtr.Zero) continue;
                    cls = IL2CPP.il2cpp_class_from_name(img, ns, type);
                    if (cls != IntPtr.Zero)
                    {
                        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_image_get_name(img));
                        L("ResolveClass(" + assembly + ", " + ns + "." + type + ") 经全量扫描命中: " + name);
                        return cls;
                    }
                }
            }
        }

        L("ResolveClass 失败: " + assembly + " :: " + ns + "." + type);
        return IntPtr.Zero;
    }

    static bool ImageNameMatches(string imageName, string wanted)
    {
        if (imageName == null || wanted == null) return false;
        if (string.Equals(imageName, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        string a = imageName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? imageName.Substring(0, imageName.Length - 4) : imageName;
        string b = wanted.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? wanted.Substring(0, wanted.Length - 4) : wanted;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

