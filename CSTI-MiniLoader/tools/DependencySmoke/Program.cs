using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSTI_MiniLoader.LoadUtil;
using LitJson;
using LZ4;
using NAudio.Wave;

namespace DependencySmoke;

internal static class Program
{
    private static int _fail;

    private static void Ok(string s)
    {
        Console.WriteLine("  [PASS] " + s);
    }

    private static void Fail(string s)
    {
        _fail++;
        Console.WriteLine("  [FAIL] " + s);
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("=== CSTI-MiniLoader 06 依赖冒烟测试 (net8.0) ===");

        TestLz4();
        TestLitJson();
        TestNAudioWav();

        if (args.Length > 0)
        {
            var pkg = args[0];
            if (File.Exists(pkg)) TestArchPackage(pkg);
            else Fail("找不到包: " + pkg);
        }
        else
        {
            Console.WriteLine("  (未提供 .modArch_V3 路径，跳过真实包解析)");
        }

        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : $"=== {_fail} FAILURES ===");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- LZ4
    private static void TestLz4()
    {
        Console.WriteLine("[1] LZ4 (lz4net " + typeof(LZ4Codec).Assembly.GetName().Version + ", " +
                          typeof(LZ4Codec).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false).Length +
                          " tfm-attr)");

        var rnd = new Random(20241002);
        var src = new byte[256 * 1024];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)(i % 13 == 0 ? i % 251 : 7);
        rnd.NextBytes(src.AsSpan(0, 4096));

        var maxOut = LZ4Codec.MaximumOutputLength(src.Length);
        var enc = new byte[maxOut];
        var encLen = LZ4Codec.Encode(src, 0, src.Length, enc, 0, enc.Length);

        // 与 mod 中完全相同的调用形式（LoadArchMod.LoadImgBLK_V2）
        var dec = new byte[src.Length];
        var decLen = LZ4Codec.Decode(enc, 0, encLen, dec, 0, dec.Length, true);

        Ok($"Encode {src.Length} -> {encLen} bytes, Decode -> {decLen} bytes (Codec={LZ4Codec.CodecName})");
        if (decLen != src.Length) Fail("解码长度不符");
        else
        {
            for (var i = 0; i < src.Length; i++)
                if (src[i] != dec[i])
                {
                    Fail("解码内容在第 " + i + " 字节不符");
                    return;
                }

            Ok("7 参 Decode 重载往返一致");
        }
    }

    // ------------------------------------------------------------- LitJSON
    private static void TestLitJson()
    {
        Console.WriteLine("[2] LitJSON");
        var data = JsonMapper.ToObject("{\"a\":1,\"b\":[true,\"x\"],\"c\":{\"d\":2.5}}");
        if (!data.IsObject || !data.ContainsKey("a") || (int)data["a"] != 1) Fail("JsonData 基本访问异常");
        else if (data["b"].Count != 2 || !data["b"][0].IsBoolean) Fail("JsonData 数组访问异常");
        else Ok("JsonData 解析/索引/类型判定正常: " + data.ToJson());
    }

    // -------------------------------------------------------------- NAudio
    private static void TestNAudioWav()
    {
        Console.WriteLine("[3] NAudio (WAV 链路) 版本 " + typeof(WaveFileReader).Assembly.GetName().Version);
        // 生成 0.1s 16bit 单声道 8kHz 静音 WAV
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            const int rate = 8000, samples = 800, ch = 1, bits = 16;
            var dataLen = samples * ch * bits / 8;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataLen);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)ch);
            w.Write(rate);
            w.Write(rate * ch * bits / 8);
            w.Write((short)(ch * bits / 8));
            w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataLen);
            for (var i = 0; i < dataLen; i++) w.Write((byte)0);
        }

        ms.Seek(0, SeekOrigin.Begin);
        try
        {
            var reader = new WaveFileReader(ms);
            var total = 0;
            while (true)
            {
                float[] frame;
                try
                {
                    frame = reader.ReadNextSampleFrame();
                }
                catch (Exception)
                {
                    break;
                }

                if (frame == null) break;
                total += frame.Length;
            }

            Ok($"WaveFileReader ok: SampleCount={reader.SampleCount} 读取采样={total} " +
               $"format={reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch");
        }
        catch (Exception e)
        {
            Fail("WAV 链路异常: " + e.GetType().Name + " " + e.Message);
        }
    }

    // ----------------------------------------------------- 真实 .modArch_V3
    private const string EndFlg = "_End_";

    private static void TestArchPackage(string path)
    {
        Console.WriteLine("[4] 真实包解析: " + Path.GetFileName(path) + " (" + new FileInfo(path).Length + " bytes)");
        try
        {
            using var fs = new BufferedStream(File.OpenRead(path), 1024 * 1024);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            var modName = br.ReadString();
            Console.WriteLine("  模组名: " + modName);

            var blk = br.ReadString();
            var imgSingle = 0;
            var imgAtlas = 0;
            var imgBytes = 0L;
            var rawLooking = 0;
            var encodedLooking = 0;
            var formats = new Dictionary<int, int>();
            var jsonItems = 0;
            var jsonKinds = new Dictionary<string, int>();
            var badJson = 0;
            var jsonSamples = new List<string>();

            while (blk != EndFlg)
            {
                var blkCount = br.ReadInt32();
                Console.WriteLine($"  区块 {blk}: {blkCount} 个子块");
                for (var i = 0; i < blkCount; i++)
                {
                    var lz4Len = br.ReadInt32();
                    var rawLen = br.ReadInt32();
                    var packed = br.ReadBytes(lz4Len);
                    var buf = new byte[rawLen];
                    var n = LZ4Codec.Decode(packed, 0, packed.Length, buf, 0, buf.Length, true);
                    Console.WriteLine($"    子块{i}: lz4={lz4Len} 解码后={n}/{rawLen}");
                    if (n != rawLen)
                    {
                        Fail($"区块 {blk}[{i}] LZ4 解码长度 {n} != 声明 {rawLen}");
                        continue;
                    }

                    using var ms = new MemoryStream(buf);
                    using var r = new BinaryReader(ms, Encoding.UTF8);
                    switch (blk)
                    {
                        case "ImgBLK":
                            while (true)
                            {
                                var itemFlg = r.ReadInt32();
                                if (itemFlg == 0) break;
                                if (itemFlg == 1)
                                {
                                    var imgName = r.ReadString();
                                    var w = r.ReadInt32();
                                    var h = r.ReadInt32();
                                    var fmt = r.ReadInt32();
                                    var len = r.ReadInt32();
                                    var data = r.ReadBytes(len);
                                    imgSingle++;
                                    imgBytes += len;
                                    formats[fmt] = formats.TryGetValue(fmt, out var c) ? c + 1 : 1;
                                    ClassifyImage(w, h, fmt, data, ref rawLooking, ref encodedLooking);
                                    if (imgSingle <= 3)
                                        Console.WriteLine(
                                            $"    单图 {Path.GetFileName(imgName)} {w}x{h} fmt={fmt} len={len} 头={Hex(data, 8)}");
                                }
                                else if (itemFlg == 2)
                                {
                                    var rects = ReadRects(r);
                                    var texName = r.ReadString();
                                    var listStr = r.ReadListStr();
                                    var fmt = r.ReadInt32();
                                    var tw = r.ReadInt32();
                                    var th = r.ReadInt32();
                                    var len = r.ReadInt32();
                                    var data = r.ReadBytes(len);
                                    imgAtlas++;
                                    imgBytes += len;
                                    formats[fmt] = formats.TryGetValue(fmt, out var c) ? c + 1 : 1;
                                    ClassifyImage(tw, th, fmt, data, ref rawLooking, ref encodedLooking);
                                    if (imgAtlas <= 2)
                                        Console.WriteLine(
                                            $"    图集 {texName} {tw}x{th} fmt={fmt} 精灵数={listStr.Count} len={len} " +
                                            $"len/(w*h)={len / (double)(tw * th):F4} len%16={len % 16} 头={Hex(data, 16)} 尾={HexTail(data, 16)}");
                                }
                                else
                                {
                                    Fail("ImgBLK 未知 itemFlg=" + itemFlg);
                                    break;
                                }
                            }

                            Console.WriteLine(ms.Position == ms.Length
                                ? $"    ImgBLK 子块{i}: 解码缓冲被完整消费"
                                : $"    ImgBLK 子块{i}: 解析后仍剩余 {ms.Length - ms.Position} 字节");
                            break;

                        case "JsonsBLK":
                            var mapper = new StringMapper();
                            while (true)
                            {
                                var itemFlg = r.ReadInt32();
                                if (itemFlg == 0) break;
                                if (itemFlg == 2)
                                {
                                    mapper.Read(r);
                                    continue;
                                }

                                var listStr = r.ReadListStr();
                                var item = MapperItem.Read(r, mapper);
                                if (item == null || !item.IsObject) continue;
                                jsonItems++;
                                var kind = listStr.Count > 0 ? listStr[0] : "(empty)";
                                jsonKinds[kind] = jsonKinds.TryGetValue(kind, out var c2) ? c2 + 1 : 1;
                                var txt = item.ToJson();
                                if (jsonSamples.Count < 2 && listStr.Count > 0 && listStr[0] == "ScriptableObject")
                                    jsonSamples.Add(txt);
                                try
                                {
                                    System.Text.Json.JsonDocument.Parse(txt);
                                }
                                catch (Exception)
                                {
                                    badJson++;
                                }
                            }

                            break;

                        default:
                            // 其它区块（AudioBLK/LocalBLK/LuaBLK/…）只验证 LZ4 层
                            break;
                    }
                }

                blk = br.ReadString();
            }

            Ok($"容器分帧 + 全部区块 LZ4 解码成功 (区块计数如上)");
            Console.WriteLine($"  ImgBLK: 单图={imgSingle} 图集={imgAtlas} 图片字节总量={imgBytes}");
            Console.WriteLine("  图片数据形态: raw(尺寸*像素宽吻合)=" + rawLooking + " / 编码格式magic(PNG/JPG)=" + encodedLooking);
            Console.Write("  TextureFormat 分布: ");
            foreach (var kv in formats) Console.Write($"{kv.Key}({FmtName(kv.Key)})x{kv.Value}  ");
            Console.WriteLine();
            Console.WriteLine($"  JsonsBLK: 对象 {jsonItems} 个; ToJson 后非合法 JSON = {badJson}");
            Console.Write("  类型分布: ");
            foreach (var kv in jsonKinds) Console.Write($"{kv.Key}x{kv.Value}  ");
            Console.WriteLine();
            if (badJson > 0) Fail("有 " + badJson + " 个对象 ToJson 产物不是合法 JSON（JsonUtility.FromJsonOverwrite 会失败）");
            else if (jsonItems > 0) Ok("所有 JsonsBLK 对象 ToJson 都是合法 JSON");
            foreach (var s in jsonSamples) Console.WriteLine("  样例: " + (s.Length > 300 ? s.Substring(0, 300) + "..." : s));
        }
        catch (Exception e)
        {
            Fail("包解析异常: " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace);
        }
    }

    private static void ClassifyImage(int w, int h, int fmt, byte[] data, ref int raw, ref int encoded)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            encoded++;
            return;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            encoded++;
            return;
        }

        // 未压缩格式的“原始像素字节数”判定
        var per = fmt switch
        {
            1 => 1, // Alpha8
            2 => 2, // ARGB4444
            3 => 3, // RGB24
            4 => 4, // RGBA32
            5 => 4, // ARGB32
            7 => 2, // RGB565
            9 => 2, // R16
            13 => 2, // RGBA4444
            14 => 4, // BGRA32
            15 => 2, // RHalf
            16 => 4, // RGHalf
            17 => 8, // RGBAHalf
            18 => 4, // RFloat
            19 => 8, // RGFloat
            20 => 16, // RGBAFloat
            _ => -1 // 压缩格式（DXT/ETC/ASTC/PVRTC…）无法用 w*h 判定
        };
        if (per > 0 && (long)w * h * per == data.Length) raw++;
        else encoded++;
    }

    private static string FmtName(int fmt)
    {
        switch (fmt)
        {
            case 1: return "Alpha8";
            case 2: return "ARGB4444";
            case 3: return "RGB24";
            case 4: return "RGBA32";
            case 5: return "ARGB32";
            case 7: return "RGB565";
            case 9: return "R16";
            case 10: return "DXT1";
            case 12: return "DXT5";
            case 13: return "RGBA4444";
            case 14: return "BGRA32";
            case 17: return "RGBAHalf";
            case 20: return "RGBAFloat";
            case 25: return "BC7";
            case 34: return "ETC_RGB4";
            case 45: return "ETC2_RGB";
            case 47: return "ETC2_RGBA8";
            case 48: return "ASTC_4x4";
            default: return "?";
        }
    }

    private static string Hex(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < n && i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString().Trim();
    }

    private static string HexTail(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (var i = Math.Max(0, b.Length - n); i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString().Trim();
    }

    private static List<string> ReadListStr(this BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var list = new List<string>(count);
        for (var i = 0; i < count; i++) list.Add(reader.ReadString());
        return list;
    }

    private static RectLite[] ReadRects(this BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var list = new RectLite[count];
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadSingle();
            var height = reader.ReadSingle();
            var x = reader.ReadSingle();
            var y = reader.ReadSingle();
            list[i] = new RectLite { x = x, y = y, width = width, height = height };
        }

        return list;
    }

    private struct RectLite
    {
        public float x, y, width, height;
    }
}
