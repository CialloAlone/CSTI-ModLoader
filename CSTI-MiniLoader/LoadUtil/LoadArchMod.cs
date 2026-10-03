using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LZ4;
using MelonLoader;
using MelonLoader.Utils;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CSTI_MiniLoader.LoadUtil;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "EmptyGeneralCatchClause")]
public static class LoadArchMod
{
    public const string EndFlg = "_End_";

    /// <summary>
    /// 从 arch 里的路径取「干净名字」。
    /// 真机踩坑：arch 里存的是 Windows 路径（D:\SteamLibrary\...\Resource\Picture\slime），
    /// 而 Android 上 Path.GetFileNameWithoutExtension 不认反斜杠 → 整条路径被当成文件名，
    /// 精灵/对象就会以整条路径为键注册，游戏按 "slime"/"entrance" 查必然查不到。
    /// </summary>
    public static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var i = raw.LastIndexOfAny(new[] { '\\', '/' });
        var s = i >= 0 ? raw.Substring(i + 1) : raw;
        var dot = s.LastIndexOf('.');
        if (dot > 0) s = s.Substring(0, dot);
        return s;
    }

    /// <summary>
    /// 只枚举游戏自身的程序集（Assembly-CSharp），不要用 AccessTools.AllTypes()。
    /// AccessTools.AllTypes() 会物化所有程序集（含 Il2Cppmscorlib / UnityEngine.* 这些巨大的
    /// IL2CPP 代理程序集），MelonLoader 自带的 Mono 在为其中某些方法取参数信息时会命中
    /// "mono_class_from_mono_type_internal: implement me 0x00" 断言并直接 abort 进程。
    /// </summary>
    public static IEnumerable<Type> TypesInGameAssembly()
    {
        var assembly = typeof(UniqueIDScriptable).Assembly;
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            MelonLogger.Warning("TypesInGameAssembly: 部分类型加载失败，已忽略：" + e.Message);
            types = e.Types;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("TypesInGameAssembly: " + e.Message);
            return Array.Empty<Type>();
        }

        return types.Where(t => t != null).Select(t => t!);
    }

    /// <summary>探针日志路径（游戏根目录，Android 上可写）。</summary>
    private static string ProbeLogPath =>
        Path.Combine(MelonEnvironment.GameRootDirectory, "csti_probe.log");

    /// <summary>立即写盘（不走 MelonLoader 日志缓冲），保证崩溃前的最后一行不丢。</summary>
    public static void PLog(string s)
    {
        try
        {
            File.AppendAllText(ProbeLogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\n");
        }
        catch (Exception __e) { MelonLogger.Warning("[LoadArchMod] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
    }

    /// <summary>
    /// 诊断探针：验证原生纹理通道（RawTexture）是否可用。
    /// 注意：这里刻意**不再**实例化托管 Texture2D —— 该类型在这游戏上会触发 Mono 缺陷并 SIGABRT。
    /// </summary>
    public static void ProbeTextureApis()
    {
        try
        {
            File.WriteAllText(ProbeLogPath, "");
            PLog("== 探针开始：原生纹理通道 RawTexture ==");
            PLog("RawTexture 实现: " + RawTextureSource + " / " + RawTextureRawSource);
            RawTexture.Log = PLog;
            PLog("-- RawTexture.Describe() --");
            PLog(RawTexture.Describe());
            PLog("-- 上述解析结果结束 --");
            PLog("new GameObject(\"probe\") ...");
            var go = new GameObject("probe");
            PLog("GameObject ok: " + go.name);
            PLog("new AnimationCurve() ...");
            var ac = new AnimationCurve();
            PLog("AnimationCurve ok");
            PLog("RawTexture.CreateTexture2D(4,4,RGBA32=4,false) ...");
            var tex = RawTexture.CreateTexture2D(4, 4, 4, false);
            PLog("RawTexture.CreateTexture2D -> " + tex + (tex == IntPtr.Zero ? "  [" + RawTexture.LastError + "]" : ""));
            if (tex != IntPtr.Zero)
            {
                // [真机结论] 托管 Texture2D.LoadRawTextureData / Sprite.Create 内部依赖的 ICall
                // (Texture2D::LoadRawTextureDataImpl / Sprite::Create) 在这个裁剪版引擎里未注册，
                // 一调用就走 Mono 未实现分支 -> SIGABRT。这里只做名字解析、不做调用。
                PLog("跳过 LoadRawTextureData/CreateSprite：依赖 ICall 缺失（会 SIGABRT）");
                PLog("  Texture2D::LoadRawTextureData = 0x" + RawTexture.ResolveIcall("UnityEngine.Texture2D::LoadRawTextureData").ToInt64().ToString("X"));
                PLog("  Texture2D::SetPixelsImpl      = 0x" + RawTexture.ResolveIcall("UnityEngine.Texture2D::SetPixelsImpl").ToInt64().ToString("X"));
                PLog("  Texture2D::ApplyImpl          = 0x" + RawTexture.ResolveIcall("UnityEngine.Texture2D::ApplyImpl").ToInt64().ToString("X"));
                PLog("  Sprite::CreateSprite          = 0x" + RawTexture.ResolveIcall("UnityEngine.Sprite::CreateSprite").ToInt64().ToString("X"));
            }

            PLog("== 探针结束 ==");
        }
        catch (Exception e)
        {
            PLog("异常: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>UnityEngine 代理程序集名（interop_out/UnityEngine.CoreModule.dll）。</summary>
    /// <summary>本机引擎是否还能创建 Sprite（真机实测：裁剪版里三个 Sprite ICall 全缺，调用会 SIGABRT）。</summary>
    public static bool SpriteCreationAvailable
    {
        get
        {
            if (_spriteProbe.HasValue) return _spriteProbe.Value;
            var ok = RawTexture.ResolveIcall("UnityEngine.Sprite::CreateSprite") != IntPtr.Zero
                  || RawTexture.ResolveIcall("UnityEngine.Sprite::Create") != IntPtr.Zero
                  || RawTexture.ResolveIcall("UnityEngine.Sprite::Internal_CreateSprite") != IntPtr.Zero;
            _spriteProbe = ok;
            PLog("[CAP] 引擎可创建 Sprite = " + ok);
            return ok;
        }
    }
    private static bool? _spriteProbe;

    private const string UnityCoreAssembly = "UnityEngine.CoreModule";

    /// <summary>注释用：RawTexture.cs 是 task-1 的逐字拷贝，RawTextureRaw.cs 是本地补充。</summary>
    private const string RawTextureSource = "RawTexture.cs = task-1 逐字拷贝";
    private const string RawTextureRawSource = "RawTextureRaw.cs = 本地补充(raw 上传 + Apply)";

    /// <summary>把 RawTexture.CreateSprite 返回的原生指针包装成托管 Sprite 代理对象。</summary>
    private static Sprite? WrapSprite(IntPtr spritePtr)
    {
        if (spritePtr == IntPtr.Zero) return null;
        try
        {
            return RawTexture.Wrap(spritePtr, UnityCoreAssembly, "UnityEngine", "Sprite") as Sprite;
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"[IMG] Wrap(Sprite) 失败: {e.GetType().Name} {e.Message}");
            return null;
        }
    }

    /// <summary>GameSourceModify 统计：本来会被写坏的游戏对象数 / 保持 inert 的条目数。</summary>
    public static int GameSourceModifyResolved, GameSourceModifyInert;

    /// <summary>为克隆体新建的容器/实例总数（回归判据）。</summary>
    public static int NeutralizeCount;

    /// <summary>二分 2a：是否跳过把 mod 对象加进游戏主数据表 DataBase.AllData。</summary>
    private static int SkipDbAddLogged;

    /// <summary>已成功加入游戏主数据表 DataBase.AllData 的 mod 对象条数（判据用）。</summary>
    public static int AllDataAddedCount;

    public static void LoadAllArchMod()
    {
        var sBuf = new StringBuilder();
        var dir = MelonEnvironment.ModsDirectory;
        MelonLogger.Msg($"[ARCH] 扫描目录: {dir}");
        foreach (var file in Directory.EnumerateFiles(dir, "*.modArch_V3",
                     SearchOption.AllDirectories))
        {
            try
            {
                MelonLogger.Msg($"[ARCH] 开始加载: {Path.GetFileName(file)}");
                var loadMod = LoadMod(file, 3);
                MelonLogger.Msg(loadMod);
                sBuf.Append(Path.GetFileNameWithoutExtension(file));
                sBuf.Append(";");
            }
            catch (Exception e)
            {
                MelonLogger.Msg($"[ARCH] 加载失败 {Path.GetFileName(file)}: {e.GetType().Name} {e.Message}");
            }
        }
        MelonLogger.Msg(sBuf);
        MelonLogger.Msg("[NEUTRAL] 总计为克隆体新建容器/实例 = " + NeutralizeCount);
        MelonLogger.Msg("[GSM] GameSourceModify 统计: 本会改到游戏对象=" + GameSourceModifyResolved
                        + " 保持inert=" + GameSourceModifyInert + "（本版一律不写游戏对象）");
    }

    public static string LoadMod(string modPath, int version)
    {
        if (!File.Exists(modPath)) return $"文件 {modPath} 不存在";
        var startTime = DateTime.Now;
        using var fileStream = new BufferedStream(File.OpenRead(modPath), 1024 * 1024);
        using var binaryReader = new BinaryReader(fileStream, Encoding.UTF8, true);
        var modName = binaryReader.ReadString();
        MelonLogger.Msg($"[ARCH] 模组名: {modName}");
        var blk = binaryReader.ReadString();
        while (blk != EndFlg)
        {
            MelonLogger.Msg($"[ARCH] 区块: {blk}");
            LoadModArchBLK(blk, binaryReader, modName, version);
            blk = binaryReader.ReadString();
        }

        var endTime = DateTime.Now;
        MelonLogger.Msg($"加载模组:{modName} 文件总用时:{endTime - startTime:g}");
        return $"加载 {modName} 成功";
    }

    /// <summary>
    /// 创建 mod 的 ScriptableObject（绕开被裁剪的 `ScriptableObject::CreateScriptableObjectInstanceFromName` 等 ICall）。
    ///
    /// 两条路径（可用 MelonPreferences `CSTI_MiniLoader/UseOwnCreationFallback` 关掉兜底）：
    ///   ① `shim`     —— 反射调 `CstiICallFix.RealShims.CreateLike`（模板克隆，历史主路径）；
    ///   ② `fallback` —— **不依赖任何 mod 的自带兜底**：按真实 il2cpp 类 `il2cpp_object_new` 建**空实例**，
    ///      随后照常走「字段初始化 + 深拷贝去共享 + warp」把数据填上。
    ///      （与 ① 的语义差异：① 克隆模板会带模板的初始字段值，② 是空实例；对 mod 对象两者都要被
    ///        NeutralizeClone 深拷贝 + warp 覆盖，因此结果等价 —— 2026-10-02 与 Lead 确认。）
    /// 为什么要 ②：CSTI-MiniLoader 曾经**硬依赖 CstiICallFix**，ICallFix 不在（或其回归导致启动崩溃）时
    /// 204 个 mod 对象一个都建不出来（实测 `增量 0`）。
    /// </summary>
    public static object CreateScriptableObjectViaShim(Type type)
    {
        if (type == null) return null;

        // ── 路径 ①：ICallFix 的真 shim ──
        if (!MiniLoader.SkipShimCreation)
        {
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type k = null;
                    try { k = a.GetType("CstiICallFix.RealShims"); } catch (Exception __e) { MelonLogger.Warning("[LoadArchMod] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }
                    if (k == null) continue;
                    var m = k.GetMethod("CreateLike",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (m == null) continue;
                    var o = m.Invoke(null, new object[] { type });
                    if (o != null)
                    {
                        ShimCreatedCount++;
                        if (ShimPathLogged.Add(type.Name))
                            MelonLogger.Msg("[CREATE] 创建路径=shim   " + type.Name + "（ICallFix.RealShims.CreateLike）");
                        return o;
                    }

                    break;   // 找到类但返回 null → 落到兜底
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[ARCH] CreateScriptableObjectViaShim(shim 路径) 失败: "
                                    + e.GetType().Name + " " + e.Message);
            }
        }

        // ── 路径 ②：自带兜底（不依赖 ICallFix）──
        if (MiniLoader.UseOwnCreationFallback)
        {
            try
            {
                var cls = Diag.NativeClassOfPublic(type);
                if (cls == IntPtr.Zero) cls = Diag.FindClassPtrByClassName(type.Name);

                // ②a 先试「模板克隆」：与 ICallFix.CreateLike 同语义（保留 Unity 原生状态最稳），
                //    但用**我们自己的**模板查找 + 已注册的 Object::Internal_CloneSingle。
                var tmpl = Diag.FindTemplatePtrByClassName(type.Name);
                if (tmpl != IntPtr.Zero)
                {
                    var fn = RawTexture.ResolveIcall("UnityEngine.Object::Internal_CloneSingle");
                    if (fn != IntPtr.Zero)
                    {
                        unsafe
                        {
                            var del = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)fn;
                            var clone = del(tmpl);
                            if (clone != IntPtr.Zero)
                            {
                                var managedClone = Activator.CreateInstance(type, new object[] { clone });
                                if (managedClone != null)
                                {
                                    FallbackCloneCount++;
                                    if (ShimPathLogged.Add(type.Name))
                                        MelonLogger.Msg("[CREATE] 创建路径=fallback-clone " + type.Name
                                                        + "（模板 0x" + tmpl.ToInt64().ToString("X")
                                                        + " → 克隆 0x" + clone.ToInt64().ToString("X") + "）");
                                    return managedClone;
                                }
                            }
                        }
                    }
                }

                // ②b 最后兜底：空实例（数据靠 NeutralizeClone + warp 填）
                if (cls != IntPtr.Zero)
                {
                    var ptr = IL2CPP.il2cpp_object_new(cls);
                    if (ptr != IntPtr.Zero)
                    {
                        var managed = Activator.CreateInstance(type, new object[] { ptr });
                        if (managed != null)
                        {
                            FallbackCreatedCount++;
                            if (ShimPathLogged.Add(type.Name))
                                MelonLogger.Msg("[CREATE] 创建路径=fallback-new " + type.Name
                                                + "（il2cpp_object_new 0x" + ptr.ToInt64().ToString("X") + "）");
                            return managed;
                        }
                    }
                }
                else if (FallbackMissLogged.Add(type.Name))
                {
                    MelonLogger.Warning("[CREATE] fallback 取不到 il2cpp 类: " + type.Name);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[ARCH] CreateScriptableObjectViaShim(fallback 路径) 失败: "
                                    + e.GetType().Name + " " + e.Message);
            }
        }

        return null;
    }

    /// <summary>两条创建路径的计数与"只报一次"的集合（判据用）。</summary>
    public static int ShimCreatedCount;
    public static int FallbackCloneCount;
    public static int FallbackCreatedCount;
    private static readonly HashSet<string> ShimPathLogged = new();
    private static readonly HashSet<string> FallbackMissLogged = new();

    public static void LoadModArchBLK(string blk, BinaryReader reader, string modName, int version)
    {
        if (version <= 2) return;
        switch (blk)
        {
            case "ImgBLK":
                LoadImgBLK_V2(reader, modName);
                break;
            case "AudioBLK":
                LoadAudioBLK(reader, modName);
                break;
            case "LocalBLK":
                LoadLocalBLK(reader, modName);
                break;
            case "JsonsBLK":
                var startTime = DateTime.Now;
                LoadJsonsBLK_V3(reader, modName);
                var endTime = DateTime.Now;
                Debug.Log($"加载模组 {modName} 中的json用时:{endTime - startTime:g}");
                break;
            case "LuaBLK":
                LoadLuaBLK(reader, modName);
                break;
        }
    }

    public static void LoadImgBLK_V2(BinaryReader reader, string modName)
    {
        var dateTime1 = DateTime.Now;
        var blkCount = reader.ReadInt32();
        MelonLogger.Msg($"[IMG] 图片块数: {blkCount}");
        for (var i = 0; i < blkCount; i++)
        {
            var lz4Len = reader.ReadInt32();
            var blkLen = reader.ReadInt32();
            MelonLogger.Msg($"[IMG] 块{i}: lz4Len={lz4Len} blkLen={blkLen}");
            var bytes = reader.ReadBytes(lz4Len);
            var buffer = new byte[blkLen];
            MelonLogger.Msg("[IMG] LZ4 解码中 ...");
            LZ4Codec.Decode(bytes, 0, bytes.Length, buffer, 0, buffer.Length, true);
            MelonLogger.Msg("[IMG] LZ4 解码完成");
            var memoryStream = new MemoryStream(buffer);
            var binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);
            while (true)
            {
                var itemFlg = binaryReader.ReadInt32();
                if (itemFlg == 0) break;
                MelonLogger.Msg($"[IMG] itemFlg={itemFlg}");
                if (itemFlg == 1)
                {
                    var ImgName = binaryReader.ReadString();
                    var sprite_name = CleanName(ImgName);
                    var width = binaryReader.ReadInt32();
                    var height = binaryReader.ReadInt32();
                    var graphicsFormat = (TextureFormat)binaryReader.ReadInt32();
                    var imgDataLen = binaryReader.ReadInt32();
                    var imgData = binaryReader.ReadBytes(imgDataLen);
                    MelonLogger.Msg($"[IMG] 单图 {sprite_name} {width}x{height} fmt={graphicsFormat} dataLen={imgDataLen}");
                    // [FIX 2026-10-02] 同图集分支：LoadRawTextureData 三个 ICall 全被裁、托管 Sprite.Create 会 SIGSEGV。
                    // 现在走「解成 RGBA32 → 编 PNG → ImageConversion::LoadImage（已注册）→ Sprite::CreateSprite_Injected（已注册）」。
                    byte[] png = null;
                    if (graphicsFormat == TextureFormat.DXT5)
                        png = IconPack.ExtractPngDxt5(imgData, width, height, 0, 0, width, height);
                    else if (graphicsFormat == TextureFormat.RGBA32 && imgDataLen >= width * height * 4)
                        png = IconPack.EncodePng(imgData, width, height);
                    else
                        MelonLogger.Warning($"[IMG] 暂不支持的纹理格式 {graphicsFormat}（{sprite_name}），跳过");

                    if (png == null)
                    {
                        MelonLogger.Warning($"[IMG] 抠图/编码失败 {sprite_name}: [{IconPack.LastError}]");
                        continue;
                    }

                    var texPtr = RawTexture.CreateTexture2D(width, height, 4 /*RGBA32*/, false);
                    if (texPtr == IntPtr.Zero || !Diag.LoadImageViaIcall(texPtr, png))
                    {
                        MelonLogger.Warning($"[IMG] 上传失败 {sprite_name}: tex=0x{texPtr.ToInt64():X}");
                        continue;
                    }

                    var spritePtr = Diag.CreateSpriteViaIcall(texPtr, 0, 0, width, height);
                    var sprite = WrapSprite(spritePtr);
                    if (sprite == null)
                    {
                        MelonLogger.Warning($"[IMG] 跳过精灵 {sprite_name}：CreateSprite/Wrap 未返回对象");
                        continue;
                    }

                    MelonLogger.Msg($"[IMG] 精灵 ✓ {sprite_name} {width}x{height} png={png.Length}B tex=0x{texPtr.ToInt64():X} sprite=0x{spritePtr.ToInt64():X}");
                    sprite.name = sprite_name;
                    if (!ItemDictionary(typeof(Sprite)).ContainsKey(sprite_name))
                        ItemDictionary(typeof(Sprite)).Add(sprite_name, sprite);
                }
                else if (itemFlg == 2)
                {
                    MelonLogger.Msg("[IMG2] ReadRects ...");
                    var rects = binaryReader.ReadRects();
                    MelonLogger.Msg($"[IMG2] ReadRects 完成: {rects.Length} 个");
                    var texture2D_name = binaryReader.ReadString();
                    var listStr = binaryReader.ReadListStr();
                    var graphicsFormat = (TextureFormat)binaryReader.ReadInt32();
                    var texPackSizeWidth = binaryReader.ReadInt32();
                    var texPackSizeHeight = binaryReader.ReadInt32();
                    var imgDataLen = binaryReader.ReadInt32();
                    MelonLogger.Msg($"[IMG2] 图集 {texture2D_name} {texPackSizeWidth}x{texPackSizeHeight} fmt={graphicsFormat} 精灵数={listStr.Count} dataLen={imgDataLen}");
                    var imgData = binaryReader.ReadBytes(imgDataLen);
                    MelonLogger.Msg("[IMG2] ReadBytes 完成，开始按 rect 抠图 ...");
                    // 不再创建整张 8192x8192 图集纹理（DXT5 且 LoadRawTextureData 被裁，建了也传不上去），
                    // 直接按每个精灵的 rect 抠小块。texPtr 保留变量给编译期使用。
                    var texPtr = IntPtr.Zero;
                    MelonLogger.Msg("[IMG2] CreateTexture2D 完成，准备按 rect 抠图（ICall 变体路线）...");
                    // [FIX 2026-10-02] 原来这里用 LoadRawTextureData + 托管 Sprite.Create：
                    //   · LoadRawTextureData / LoadRawTextureDataImpl(Array) 三个 ICall 全部被裁 → 上传必然失败
                    //   · 托管 Sprite.Create 内部解析的也是不存在的名字 → 真机 SIGSEGV
                    // 现在改成：按 rect 从 DXT5 图集抠出 RGBA32 → 编 PNG → ImageConversion::LoadImage（已注册 ✓）
                    //          → Sprite::CreateSprite_Injected（已注册 ✓）建精灵。全程只用 IntPtr。
                    int okSprites = 0, failSprites = 0;
                    for (var j = 0; j < listStr.Count; j++)
                    {
                        try
                        {
                            var sprite_name = CleanName(listStr[j]);
                            var rect = rects[j];
                            var rect1 = new Rect(rect.x * texPackSizeWidth, rect.y * texPackSizeHeight,
                                rect.width * texPackSizeWidth, rect.height * texPackSizeHeight);
                            var rx = (int)Math.Round(rect1.x);
                            var ry = (int)Math.Round(rect1.y);
                            var rw = (int)Math.Round(rect1.width);
                            var rh = (int)Math.Round(rect1.height);
                            if (rw <= 0 || rh <= 0)
                            {
                                failSprites++;
                                continue;
                            }

                            var png = IconPack.ExtractPngDxt5(imgData, texPackSizeWidth, texPackSizeHeight,
                                rx, ry, rw, rh);
                            if (j < 4)
                                MelonLogger.Msg($"[IMG2] 抠图#{j} {sprite_name} rect=({rx},{ry},{rw},{rh}) png={(png?.Length ?? -1)}B"
                                                + (png == null ? " [" + IconPack.LastError + "]" : " " + IconPack.LastStats));

                            if (png == null)
                            {
                                failSprites++;
                                if (failSprites <= 3)
                                    MelonLogger.Warning($"[IMG2] 抠图失败 {sprite_name}: [{IconPack.LastError}]");
                                continue;
                            }

                            // 每张精灵用一张独立小纹理（RGBA32），避免 8192² 全解码
                            var smallTex = RawTexture.CreateTexture2D(rw, rh, 4 /*RGBA32*/, false);
                            if (j < 4) MelonLogger.Msg($"[IMG2]  纹理=0x{smallTex.ToInt64():X}");
                            if (smallTex == IntPtr.Zero)
                            {
                                failSprites++;
                                continue;
                            }

                            var okUp = Diag.LoadImageViaIcall(smallTex, png);
                            if (j < 4) MelonLogger.Msg($"[IMG2]  ICall LoadImage={okUp}");
                            if (!okUp)
                            {
                                failSprites++;
                                if (failSprites <= 3)
                                    MelonLogger.Warning($"[IMG2] 上传失败 {sprite_name}: tex=0x{smallTex.ToInt64():X} [{RawTexture.LastError}]");
                                continue;
                            }

                            var spritePtr = Diag.CreateSpriteViaIcall(smallTex, 0, 0, rw, rh);
                            if (j < 4) MelonLogger.Msg($"[IMG2]  sprite=0x{spritePtr.ToInt64():X}");
                            var sprite = WrapSprite(spritePtr);
                            if (sprite == null)
                            {
                                failSprites++;
                                continue;
                            }

                            sprite.name = sprite_name;
                            if (!ItemDictionary(typeof(Sprite)).ContainsKey(sprite_name))
                                ItemDictionary(typeof(Sprite)).Add(sprite_name, sprite);
                            // ★ [名字索引登记] mod 自建 sprite 也要能被"按名引用"找到（否则 `amber`/`amber_necklace`
                            //   这类 mod 独有名字永远解析不到 ✗）。通用：所有 mod 资产一视同仁 ✓。
                            Diag.NoteNameIndex("Sprite", sprite_name, sprite);
                            okSprites++;
                            if (okSprites <= 6)
                                MelonLogger.Msg($"[IMG2] 精灵 ✓ {sprite_name} {rw}x{rh} png={png.Length}B tex=0x{smallTex.ToInt64():X} sprite=0x{spritePtr.ToInt64():X} 纹理回读=0x{Diag.GetSpriteTextureViaIcall(spritePtr).ToInt64():X}");
                        }
                        catch (Exception ex)
                        {
                            failSprites++;
                            if (failSprites <= 3) MelonLogger.Warning("[IMG2] 精灵异常: " + ex.GetType().Name + " " + ex.Message);
                        }
                    }

                    MelonLogger.Msg($"[IMG2] 精灵创建完成: 成功={okSprites} 失败={failSprites} / 共 {listStr.Count}");
                    // 图集本身（DXT5）不再需要：留着只会白占 89MB
                    texPtr = IntPtr.Zero;
                }
            }
        }

        var dateTime2 = DateTime.Now;
        Debug.Log($"加载模组{modName}中的图片总用时为: {dateTime2 - dateTime1:g}");
    }

    public static void LoadLuaBLK(BinaryReader reader, string modName)
    {
        var blkCount = reader.ReadInt32();
        for (var i = 0; i < blkCount; i++)
        {
            var lz4Len = reader.ReadInt32();
            var blkLen = reader.ReadInt32();
            var bytes = reader.ReadBytes(lz4Len);
            var buffer = new byte[blkLen];
            LZ4Codec.Decode(bytes, 0, bytes.Length, buffer, 0, buffer.Length, true);
            var memoryStream = new MemoryStream(buffer);
            var binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);
            while (true)
            {
                var itemFlg = binaryReader.ReadInt32();
                if (itemFlg == 0) break;
                var listStr = binaryReader.ReadListStr();
                var lua = binaryReader.ReadString();
                if (listStr.Count < 2) continue;
                if (AllLuaFiles.TryGetValue(listStr[0], out var dictionary))
                {
                    dictionary[modName + "_" + string.Join("|", listStr.GetRange(1, listStr.Count - 1))] = lua;
                }
                else
                {
                    AllLuaFiles[listStr[0]] = new Dictionary<string, string>
                    {
                        [modName + "_" + string.Join("|", listStr.GetRange(1, listStr.Count - 1))] = lua
                    };
                }
            }
        }
    }

    public static void LoadJsonsBLK_V3(BinaryReader reader, string modName)
    {
        MelonLogger.Msg("[ARCH] 枚举游戏程序集类型 ...");
        var gameTypes = TypesInGameAssembly().ToList();
        MelonLogger.Msg($"[ARCH] 类型总数: {gameTypes.Count}");
        var allUniqueIDScriptableTypes = (from type in gameTypes
            where type.IsSubclassOf(typeof(UniqueIDScriptable))
            select type).ToList();
        MelonLogger.Msg($"[ARCH] UniqueIDScriptable 子类: {allUniqueIDScriptableTypes.Count}");
        var allScriptableObjectTypes = (from type in gameTypes
            where type.IsSubclassOf(typeof(ScriptableObject))
            where !type.IsSubclassOf(typeof(UniqueIDScriptable))
            where type != typeof(UniqueIDScriptable)
            select type).ToList();
        MelonLogger.Msg($"[ARCH] ScriptableObject 子类: {allScriptableObjectTypes.Count}");
        var blkCount = reader.ReadInt32();
        for (var i = 0; i < blkCount; i++)
        {
            var lz4Len = reader.ReadInt32();
            var blkLen = reader.ReadInt32();
            var bytes = reader.ReadBytes(lz4Len);
            var buffer = new byte[blkLen];
            LZ4Codec.Decode(bytes, 0, bytes.Length, buffer, 0, buffer.Length, true);
            var memoryStream = new MemoryStream(buffer);
            var binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);
            var mapper = new StringMapper();
            while (true)
            {
                var itemFlg = binaryReader.ReadInt32();
                if (itemFlg == 0) break;
                if (itemFlg == 2)
                {
                    mapper.Read(binaryReader);
                    continue;
                }

                var listStr = binaryReader.ReadListStr();
                var mapperItem = MapperItem.Read(binaryReader, mapper);
                if (mapperItem == null) continue;
                if (!mapperItem.IsObject) continue;
                var mapperObject = (MapperObject)mapperItem;
                if (listStr.Count == 0) continue;
                if (listStr[0] == "ModInfo.json")
                {
                    // Debug.Log($"正在加载打包模组:{modName}");
                }
                else if (listStr[0] == "ScriptableObject")
                {
                    if (listStr.Count < 3) continue;
                    var obj_name = CleanName(listStr.Last());
                    var find_ScriptableObjectT =
                        allScriptableObjectTypes.FirstOrDefault(type1 => type1.Name == listStr[1]);
                    var dict = ItemDictionary(find_ScriptableObjectT);
                    if (dict.ContainsKey(obj_name))
                        continue;

                    var obj = CreateScriptableObjectViaShim(find_ScriptableObjectT) as ScriptableObject;
                    if (obj == null) obj = ScriptableObject.CreateInstance(Il2CppType.From(find_ScriptableObjectT));

                    obj.name = obj_name;
                    // [回归根治 2026-10-02] 先把克隆体身上所有引用类型数据字段换成**全新的空实例**：
                    // 克隆是浅拷贝（字段与游戏资产共享同一批对象），不先断开的话，后面的
                    // FromJsonOverwrite / warp / FillDropsList 就会把 mod 数据写进游戏自带的集合与子对象里
                    // → 用户实测的「开局事件选药物没给东西、探索没掉落、改天气无效」。
                    NeutralizeCount += Diag.NeutralizeClone(obj, "[ARCH]");
                    try
                    {
                        JsonUtility.FromJsonOverwrite(mapperObject.ToJson(), obj);
                    }
                    catch (Exception)
                    {
                    }

                    if (!dict.ContainsKey(obj_name))
                        dict.Add(obj_name, obj);
                    WaitForWarpperEditorNoGuidList.Add(new ScriptableObjectPack(obj,
                        "", "", modName, mapperObject));
                    RegObj(obj_name, obj, find_ScriptableObjectT);
                }
                else if (listStr[0] == "GameSourceModify")
                {
                    // [2026-10-03 修复] GSM = 「改造**游戏自带**卡牌」的路径 —— Windy 的"精灵能力"就靠它
                    // （采摘柠檬草/芦荟/卡瓦/大叶仙茅/蜘蛛兰/椰子树…，纤维缠细线、棕榈叶编织、泥堆→粘土…）。
                    // PC 侧语义（CSTI-ModLoader/ModLoader.cs:1243）：
                    //   var Guid = Path.GetFileNameWithoutExtension(file);   // 文件名 = 目标卡 GUID
                    //   AllGUIDDict.TryGetValue(Guid, out var obj) ? Pack(obj,…) : Pack(null, Guid, …)
                    // PC 上 AllGUIDDict 含**全部**卡牌（游戏自带 + mod）→ 能直接命中；
                    // 我们的移植版 AllGUIDDict **只登记 mod 对象**（游戏自带对象在
                    // UniqueIDScriptable.AllUniqueObjects，当初又用 typeof(UniqueIDScriptable) 注册，
                    // 被 RegObj 的子类判定挡掉）→ 63 条 GSM 全部 inert → 精灵能力全失效。
                    // 修法：先查 AllGUIDDict，查不到再查**游戏注册表** —— 与 PC 语义对齐。
                    var guid = CleanName(listStr.Last());
                    ScriptableObject gsmTarget = null;
                    string gsmFrom;
                    if (AllGUIDDict.TryGetValue(guid, out var gsmMod))
                    {
                        gsmTarget = gsmMod;
                        gsmFrom = "mod字典";
                    }
                    else
                    {
                        gsmFrom = "未解析";
                        var reg = UniqueIDScriptable.AllUniqueObjects;
                        if (reg != null && reg.TryGetValue(guid, out var gsmGame) && gsmGame != null)
                        {
                            gsmTarget = gsmGame;
                            gsmFrom = "游戏注册表";
                        }
                    }

                    if (gsmTarget != null)
                    {
                        GameSourceModifyResolved++;
                        if (GameSourceModifyResolved <= 12)
                            MelonLogger.Msg("[GSM] 目标已解析(" + gsmFrom + "): " + Diag.Cls(gsmTarget) + " / "
                                            + Diag.NameOf(gsmTarget) + " / GUID=" + guid);
                    }
                    else
                    {
                        GameSourceModifyInert++;
                        if (GameSourceModifyInert <= 8)
                            MelonLogger.Warning("[GSM] 目标未解析（mod 字典与游戏注册表都没有）: GUID=" + guid);
                    }

                    WaitForWarpperEditorGameSourceGUIDList.Add(
                        new ScriptableObjectPack(gsmTarget, guid, "", modName, mapperObject));
                }
                else
                {
                    var type = allUniqueIDScriptableTypes.FirstOrDefault(type => type.Name == listStr[0]);
                    if (type == null) continue;
                    var CardName = CleanName(listStr.Last());
                    try
                    {
                        if (!(mapperObject.ContainsKey("UniqueID") && mapperObject["UniqueID"].IsString &&
                              !string.IsNullOrEmpty(mapperObject["UniqueID"].ToString())))
                        {
                            continue;
                        }

                        var card = CreateScriptableObjectViaShim(type) as UniqueIDScriptable;
                        if (card == null) card = ScriptableObject.CreateInstance(Il2CppType.From(type)) as UniqueIDScriptable;
                        // [回归根治 2026-10-02] 同 ScriptableObject 分支：先断开与游戏资产共享的引用字段
                        NeutralizeCount += Diag.NeutralizeClone(card, "[ARCH]");
                        // JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(card), card);
                        try
                        {
                            JsonUtility.FromJsonOverwrite(mapperObject.ToJson(), card);
                        }
                        catch (Exception)
                        {
                        }

                        card!.name = $"{modName}_{CardName}";

                        //type.GetMethod("Init", bindingFlags, null, new Type[] { }, null).Invoke(card, null);
                        var card_guid = card.UniqueID;
                        if (!AllGUIDDict.ContainsKey(card_guid))
                        {
                            AllGUIDDict.Add(card_guid, card);
                            // [2026-10-02] 「是否写游戏主数据表 DataBase.AllData」现在是**开关**
                            // （MelonPreferences: CSTI_MiniLoader/AddToGameDataBase，默认 true）。
                            // 历史：当初"事件选了没给东西"的真凶是**浅拷贝共享污染**（嵌套 warp 原地改共享对象），
                            // 已由「递归深拷贝 + 重开嵌套 warp」根治；"不写 AllData"只是当时的二分手段。
                            // 现在深拷贝在位、[INVARIANT] 长期 0 变化，所以默认加回来
                            // —— 猜测控制台/UI 列卡片正是遍历这张游戏主数据表。
                            if (MiniLoader.SkipGameDataBaseAdd)
                            {
                                if (SkipDbAddLogged++ == 0)
                                    MelonLogger.Msg("[DB] AddToGameDataBase=false：跳过 GameLoad.Instance.DataBase.AllData.Add(card)"
                                                    + "（mod 对象仍进 AllGUIDDict / 自建字典 / 游戏注册表 Init）");
                            }
                            else
                            {
                                try
                                {
                                    GameLoad.Instance.DataBase.AllData.Add(card);
                                    AllDataAddedCount++;
                                }
                                catch (Exception adde)
                                {
                                    MelonLogger.Warning("[DB] AllData.Add 失败: " + adde.GetType().Name + " " + adde.Message);
                                }
                            }
                        }

                        try { Diag.ModCardJsonSource[card_guid] = mapperObject; } catch (Exception __e) { MelonLogger.Warning("[LoadArchMod] 异常(已记录): " + __e.GetType().Name + " " + __e.Message); }   // 诊断：留下作者 JSON 供对账
                        if (!WaitForWarpperEditorGuidDict.ContainsKey(card_guid))
                            WaitForWarpperEditorGuidDict.Add(card_guid,
                                new ScriptableObjectPack(card, "", "", modName,
                                    mapperObject));
                        RegObj(card_guid, card, type);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }

    public static void LoadLocalBLK(BinaryReader reader, string modName)
    {
        var blkCount = reader.ReadInt32();
        for (var i = 0; i < blkCount; i++)
        {
            var lz4Len = reader.ReadInt32();
            var blkLen = reader.ReadInt32();
            var bytes = reader.ReadBytes(lz4Len);
            var buffer = new byte[blkLen];
            LZ4Codec.Decode(bytes, 0, bytes.Length, buffer, 0, buffer.Length, true);
            var memoryStream = new MemoryStream(buffer);
            var binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);
            while (true)
            {
                var LocalName = binaryReader.ReadString();
                if (LocalName == EndFlg) break;
                var LocalContent = binaryReader.ReadString();

                // [2026-10-03 通用化] 记录"**来自 mod 包**的本地化键"（按来源，不按名字）——
                // 供 [L10N] 判据统计"注入键数 / 其中有几条真的进了 LocalizationManager"，
                // 对任何 mod 都成立，不需要知道 mod 叫什么。
                Diag.NoteModLocalizationKey(LocalName);

                WaitForLoadCSVList.Add((LocalName, LocalContent));
            }
        }
    }

    public static void LoadAudioBLK(BinaryReader reader, string modName)
    {
        var blkCount = reader.ReadInt32();
        for (var i = 0; i < blkCount; i++)
        {
            var lz4Len = reader.ReadInt32();
            var blkLen = reader.ReadInt32();
            var bytes = reader.ReadBytes(lz4Len);
            var buffer = new byte[blkLen];
            LZ4Codec.Decode(bytes, 0, bytes.Length, buffer, 0, buffer.Length, true);
            var memoryStream = new MemoryStream(buffer);
            var binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);
            while (true)
            {
                var AudioName = binaryReader.ReadString();
                if (AudioName == EndFlg)
                {
                    break;
                }

                var stream = new MemoryStream();
                var audioDataLen = binaryReader.ReadInt32();
                var audioData = binaryReader.ReadBytes(audioDataLen);
                stream.Write(audioData, 0, audioDataLen);
                stream.Seek(0, SeekOrigin.Begin);
                // Android 适配：NAudio 的 MP3 解码依赖 Windows ACM/DMO（winmm），在 Android 上会抛
                // DllNotFoundException/TypeLoadException。原版没有 try/catch，单个音频失败会中断整个
                // 包的加载；这里按“逐条隔离”处理（读取已完成，流位置安全），只跳过这一条音频。
                try
                {
                    if (AudioName.EndsWith(".wav", true, null))
                    {
                        var clip_name = Path.GetFileNameWithoutExtension(AudioName);
                        var clip = ResourceDataLoader.GetAudioClipFromWav(stream, clip_name);
                        if (!clip) continue;
                        RegObj(clip_name, clip, typeof(AudioClip));
                    }
                    else if (AudioName.EndsWith(".mp3", true, null))
                    {
                        var clip_name = Path.GetFileNameWithoutExtension(AudioName);
                        var clip = ResourceDataLoader.GetAudioClipFromMp3(stream, clip_name);
                        if (!clip) continue;
                        RegObj(clip_name, clip, typeof(AudioClip));
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning(
                        $"[AUDIO] {AudioName} 解码失败（已跳过该条，不影响其它区块）: {e.GetType().Name} {e.Message}");
                }
            }
        }
    }
}