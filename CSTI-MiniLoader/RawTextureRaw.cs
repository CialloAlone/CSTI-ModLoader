// ============================================================================
//  RawTextureRaw —— RawTexture.cs 的补充：原始 GPU 纹理字节上传 + Apply
// ----------------------------------------------------------------------------
//  为什么需要单独一个文件：
//    RawTexture.cs 是 task-1（raw-interop）交付的固定 API，本工程**逐字**拷贝自
//        D:\RiderProjects\ml-installer-06\mods-06\RawTextureTest\RawTexture.cs
//    以保持“重新拷贝即可同步”。它提供 CreateTexture2D(int,int,int,bool) / LoadImage
//    (PNG/JPG 语义) / CreateSprite / Wrap，但**没有**原始字节上传。
//    而实测 .modArch_V3 的 ImgBLK 是原始 GPU 纹理字节（8192x8192 DXT5，头部
//    FF FF 49 92 24 49 92 24 是合法 DXT5 alpha 块），ImageConversion.LoadImage 无法承载。
//    所以这里按同样的“全原生 IntPtr、绝不出现托管 Texture2D”风格补两个方法：
//        bool LoadRawTextureData(IntPtr tex, byte[] data)
//        bool Apply(IntPtr tex, bool updateMipmaps, bool makeNoLongerReadable)
//    对应原 0.5.7 代码的 t2d.LoadRawTextureData(imgData) + t2d.Apply(false,false)。
//
//  待归并：raw-interop 把这两个方法并进 RawTexture.cs 之后，本文件即可删除，
//          调用点把 RawTextureRaw.X 改回 RawTexture.X 即可（共 4 处，见 LoadArchMod.cs）。
//
//  实现要点（沿用 RawTexture.cs / NOTES.md 里已核对过的结论）：
//    - 引擎程序集名带 .dll 后缀，否则 IL2CPP.GetIl2CppClass 返回 0；
//    - 用 Texture2D.LoadRawTextureData(IntPtr, int) 重载：pin 住托管 byte[] 直接传指针，
//      不需要自己构造 IL2CPP byte[]（也就不需要解析 System.Byte 类，少一条真机坑）；
//    - il2cpp_runtime_invoke 调实例方法时 obj 传对象本身（不是 IntPtr.Zero）；
//    - 返回 void 的方法不用 unbox。
// ============================================================================

using System;
using Il2CppInterop.Runtime;

/// <summary>RawTexture 的补充：原始纹理字节上传 + Apply（全原生通道）。</summary>
public static class RawTextureRaw
{
    /// <summary>最近一次失败原因；成功时为 null。</summary>
    public static string LastError { get; private set; }

    /// <summary>引擎程序集名必须带 .dll 后缀（raw-interop 真机结论）。</summary>
    public const string CoreModule = "UnityEngine.CoreModule.dll";

    static IntPtr _clsTexture2D;
    static IntPtr _mLoadRawPtr; // Texture2D.LoadRawTextureData(IntPtr, int)
    static IntPtr _mApply;      // Texture2D.Apply(bool, bool)

    static void L(string msg)
    {
        try { RawTexture.Log?.Invoke(msg); } catch { /* 日志失败不影响主流程 */ }
    }

    static string Hex(IntPtr p) => p.ToInt64().ToString("X");

    static IntPtr GetTexture2DClass()
    {
        if (_clsTexture2D == IntPtr.Zero)
            _clsTexture2D = IL2CPP.GetIl2CppClass(CoreModule, "UnityEngine", "Texture2D");
        if (_clsTexture2D == IntPtr.Zero)
            _clsTexture2D = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule", "UnityEngine", "Texture2D");
        return _clsTexture2D;
    }

    /// <summary>
    /// 把原始 GPU 纹理字节写入纹理（等价 <c>Texture2D.LoadRawTextureData(byte[])</c>）。
    /// 数据必须与创建纹理时指定的 TextureFormat/尺寸匹配。
    /// </summary>
    public static bool LoadRawTextureData(IntPtr tex, byte[] data)
    {
        LastError = null;
        if (tex == IntPtr.Zero) { LastError = "纹理指针为 0"; return false; }
        if (data == null || data.Length == 0) { LastError = "数据为空"; return false; }

        var cls = GetTexture2DClass();
        if (cls == IntPtr.Zero) { LastError = "找不到 Texture2D 类（" + CoreModule + "）"; return false; }

        if (_mLoadRawPtr == IntPtr.Zero)
            _mLoadRawPtr = IL2CPP.il2cpp_class_get_method_from_name(cls, "LoadRawTextureData", 2);
        if (_mLoadRawPtr == IntPtr.Zero)
        {
            LastError = "找不到 Texture2D.LoadRawTextureData(IntPtr, int)";
            L("LoadRawTextureData 失败: " + LastError);
            return false;
        }

        unsafe
        {
            fixed (byte* p = data)
            {
                IntPtr ptrLocal = (IntPtr)p;
                int sizeLocal = data.Length;
                var args = stackalloc IntPtr[2];
                args[0] = (IntPtr)(&ptrLocal);
                args[1] = (IntPtr)(&sizeLocal);

                IntPtr exc = IntPtr.Zero;
                _ = IL2CPP.il2cpp_runtime_invoke(_mLoadRawPtr, tex, (void**)args, ref exc);
                if (exc != IntPtr.Zero)
                {
                    LastError = "LoadRawTextureData 抛异常: " + RawTexture.DescribeException(exc);
                    L("LoadRawTextureData(tex=0x" + Hex(tex) + ", " + data.Length + " 字节) 失败: " + LastError);
                    return false;
                }
            }
        }

        L("LoadRawTextureData(tex=0x" + Hex(tex) + ", " + data.Length + " 字节) -> ok");
        return true;
    }

    /// <summary>把纹理数据真正上传到 GPU（等价 <c>Texture2D.Apply(updateMipmaps, makeNoLongerReadable)</c>）。</summary>
    public static bool Apply(IntPtr tex, bool updateMipmaps, bool makeNoLongerReadable)
    {
        LastError = null;
        if (tex == IntPtr.Zero) { LastError = "纹理指针为 0"; return false; }

        var cls = GetTexture2DClass();
        if (cls == IntPtr.Zero) { LastError = "找不到 Texture2D 类（" + CoreModule + "）"; return false; }

        if (_mApply == IntPtr.Zero)
            _mApply = IL2CPP.il2cpp_class_get_method_from_name(cls, "Apply", 2);
        if (_mApply == IntPtr.Zero)
        {
            LastError = "找不到 Texture2D.Apply(bool, bool)";
            L("Apply 失败: " + LastError);
            return false;
        }

        unsafe
        {
            bool mip = updateMipmaps;
            bool noRead = makeNoLongerReadable;
            var args = stackalloc IntPtr[2];
            args[0] = (IntPtr)(&mip);
            args[1] = (IntPtr)(&noRead);

            IntPtr exc = IntPtr.Zero;
            _ = IL2CPP.il2cpp_runtime_invoke(_mApply, tex, (void**)args, ref exc);
            if (exc != IntPtr.Zero)
            {
                LastError = "Apply 抛异常: " + RawTexture.DescribeException(exc);
                L("Apply(tex=0x" + Hex(tex) + ") 失败: " + LastError);
                return false;
            }
        }

        L("Apply(tex=0x" + Hex(tex) + ", mip=" + updateMipmaps + ", noLongerReadable=" + makeNoLongerReadable + ") -> ok");
        return true;
    }
}
