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
        catch
        {
        }
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

    /// <summary>通过 CstiICallFix.RealShims.CreateLike 克隆式创建（绕开被裁剪的 ScriptableObject ICall）。</summary>
    public static object CreateScriptableObjectViaShim(Type type)
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type k = null;
                try { k = a.GetType("CstiICallFix.RealShims"); } catch { }
                if (k == null) continue;
                var m = k.GetMethod("CreateLike", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m == null) continue;
                var o = m.Invoke(null, new object[] { type });
                if (o != null) return o;
                break;
            }
        }
        catch (Exception e) { MelonLogger.Warning("[ARCH] CreateScriptableObjectViaShim 失败: " + e.Message); }
        return null;
    }

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
                    // [回归修复 2026-10-02] 这里**故意**还原成「整条路径当键」（= inert）：
                    // 用 CleanName 之后这些条目会真的解析到**游戏自带对象**，mod 数据被写进原版事件/卡牌/天气
                    // 相关对象 —— 用户实测「开局事件选药物没给东西、改天气无效」，判定为回归。
                    // 在能把 mod 写入与游戏对象彻底隔离之前，这条链路保持与"正常版本"一致的行为。
                    // 但仍用 CleanName 做一次**只读**判断，记录它本来会改到哪些对象，便于后续决策。
                    var rawKey = Path.GetFileNameWithoutExtension(listStr.Last());
                    if (AllGUIDDict.TryGetValue(CleanName(listStr.Last()), out var wouldTarget))
                    {
                        GameSourceModifyResolved++;
                        if (GameSourceModifyResolved <= 8)
                            MelonLogger.Warning("[GSM] 本会被修改的游戏对象: " + Diag.Cls(wouldTarget) + " / "
                                                + Diag.NameOf(wouldTarget) + " / GUID=" + CleanName(listStr.Last()));
                    }
                    else
                    {
                        GameSourceModifyInert++;
                    }

                    WaitForWarpperEditorGameSourceGUIDList.Add(
                        new ScriptableObjectPack(null, rawKey, "", modName, mapperObject));
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
                            // [二分 2a 2026-10-02] 不再把 mod 对象塞进**游戏自己的主数据表**。
                            // 游戏的事件/掉落结算会遍历 DataBase.AllData，撞上我们这些
                            // 「空容器 + 字段不全」的克隆卡就可能中途异常 → 选了没反应 / 没掉落。
                            if (MiniLoader.SkipGameDataBaseAdd)
                            {
                                if (SkipDbAddLogged++ == 0)
                                    MelonLogger.Msg("[DB] 二分 2a：跳过 GameLoad.Instance.DataBase.AllData.Add(card)"
                                                    + "（mod 对象仍进 AllGUIDDict / 自建字典 / 游戏注册表 Init）");
                            }
                            else
                            {
                                GameLoad.Instance.DataBase.AllData.Add(card);
                            }
                        }

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