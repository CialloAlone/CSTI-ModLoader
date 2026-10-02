// ============================================================================
//  IconPack —— 从 mod 图集里按 rect 抠出图标并编成 PNG
// ----------------------------------------------------------------------------
//  为什么需要它：
//    Windy 的 ImgBLK 是 8192x8192 的 **DXT5** 图集（8192*8192=64Mi 基级 + mip 链，
//    dataLen=89478512 = base*4/3），原版靠 Texture2D.LoadRawTextureData 直接上传 GPU 字节，
//    但本机这个 ICall 被裁了（连 LoadRawTextureDataImpl/ImplArray 都没有）。
//    可用的上传口只剩：
//      · UnityEngine.ImageConversion::LoadImage        （已注册 ✓，吃 PNG/JPG 字节）
//      · UnityEngine.Texture2D::SetPixelsImpl          （已注册 ✓，吃 Color[]）
//      · UnityEngine.Texture2D::ApplyImpl              （已注册 ✓）
//      · UnityEngine.Sprite::CreateSprite_Injected     （已注册 ✓）
//    所以这里把图集**按精灵 rect 小块解码**成 RGBA32，再编成 PNG，交给 LoadImage。
//    只解 64x64 这种小图，完全避开「解码整张 8192²」（268MB、C# 会卡死）的坑。
//
//  DXT5/BC3 块格式（16 字节/4x4 块）：
//    [0]=alpha0 [1]=alpha1 [2..7]=16*3bit alpha 索引
//    [8..9]=color0(RGB565 LE) [10..11]=color1 [12..15]=16*2bit 颜色索引
// ============================================================================

using System;
using System.IO;
using System.IO.Compression;

namespace CSTI_MiniLoader
{
    public static class IconPack
    {
        /// <summary>最近一次失败原因。</summary>
        public static string LastError { get; private set; }

        /// <summary>最近一次解码的像素统计（判断抠出来的图是不是有效：平均 alpha 是否接近 0 等）。</summary>
        public static string LastStats { get; private set; }

        /// <summary>把图集里 (x,y,w,h) 这块 DXT5 数据解成 RGBA32，并编成 PNG 字节。</summary>
        public static byte[] ExtractPngDxt5(byte[] atlas, int atlasW, int atlasH, int x, int y, int w, int h)
        {
            LastError = null;
            LastStats = null;
            if (atlas == null || atlas.Length == 0) { LastError = "图集数据为空"; return null; }
            if (w <= 0 || h <= 0) { LastError = "区域尺寸非法 " + w + "x" + h; return null; }
            if (x < 0 || y < 0 || x + w > atlasW || y + h > atlasH)
            {
                LastError = "区域越界 (" + x + "," + y + "," + w + "," + h + ") 图集 " + atlasW + "x" + atlasH;
                return null;
            }

            var rgba = new byte[w * h * 4];
            if (!DecodeDxt5Region(atlas, atlasW, atlasH, x, y, w, h, rgba))
            {
                return null;
            }

            // 像素统计：alpha 全 0 / 颜色全黑 说明解码有问题（图标会看不见）
            long sr = 0, sg = 0, sb = 0, sa = 0, nz = 0;
            var px = w * h;
            for (var i = 0; i < px; i++)
            {
                sr += rgba[i * 4 + 0];
                sg += rgba[i * 4 + 1];
                sb += rgba[i * 4 + 2];
                sa += rgba[i * 4 + 3];
                if (rgba[i * 4 + 3] > 8) nz++;
            }

            LastStats = "均值 RGBA=(" + (sr / px) + "," + (sg / px) + "," + (sb / px) + "," + (sa / px)
                        + ") 有效像素=" + (nz * 100 / px) + "%";

            return EncodePng(rgba, w, h);
        }

        /// <summary>DXT5 解码指定像素区域（内部按 4x4 块对齐解码再裁剪）。</summary>
        public static bool DecodeDxt5Region(byte[] atlas, int atlasW, int atlasH, int x, int y, int w, int h, byte[] rgba)
        {
            LastError = null;
            var blocksPerRow = atlasW / 4;
            var totalBlocks = (long)(atlasW / 4) * (atlasH / 4) * 16;
            if (totalBlocks > atlas.Length)
            {
                LastError = "图集数据不足：需要 " + totalBlocks + " 字节，实际 " + atlas.Length;
                return false;
            }

            var bx0 = x / 4;
            var by0 = y / 4;
            var bx1 = (x + w + 3) / 4;
            var by1 = (y + h + 3) / 4;

            var alpha = new byte[8];
            var cr = new byte[4];
            var cg = new byte[4];
            var cb = new byte[4];
            var ca = new byte[4];

            for (var by = by0; by < by1; by++)
            {
                for (var bx = bx0; bx < bx1; bx++)
                {
                    var off = (by * blocksPerRow + bx) * 16;
                    DecodeAlphaRamp(atlas[off], atlas[off + 1], alpha);

                    // 16 个 3bit alpha 索引（6 字节小端）
                    ulong aIdx = 0;
                    for (var i = 0; i < 6; i++) aIdx |= (ulong)atlas[off + 2 + i] << (8 * i);

                    var c0 = (ushort)(atlas[off + 8] | (atlas[off + 9] << 8));
                    var c1 = (ushort)(atlas[off + 10] | (atlas[off + 11] << 8));
                    DecodeColorRamp(c0, c1, cr, cg, cb, ca);

                    uint cIdx = (uint)(atlas[off + 12] | (atlas[off + 13] << 8) | (atlas[off + 14] << 16) | (atlas[off + 15] << 24));

                    for (var i = 0; i < 16; i++)
                    {
                        var px = bx * 4 + (i % 4);
                        var py = by * 4 + (i / 4);
                        if (px < x || py < y || px >= x + w || py >= y + h) continue;

                        var ci = (int)((cIdx >> (2 * i)) & 3);
                        var ai = (int)((aIdx >> (3 * i)) & 7);
                        var dst = ((py - y) * w + (px - x)) * 4;
                        rgba[dst + 0] = cr[ci];
                        rgba[dst + 1] = cg[ci];
                        rgba[dst + 2] = cb[ci];
                        rgba[dst + 3] = alpha[ai];
                    }
                }
            }

            return true;
        }

        private static void DecodeAlphaRamp(byte a0, byte a1, byte[] out8)
        {
            out8[0] = a0;
            out8[1] = a1;
            if (a0 > a1)
            {
                for (var i = 2; i < 8; i++)
                    out8[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7);
            }
            else
            {
                for (var i = 2; i < 6; i++)
                    out8[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5);
                out8[6] = 0;
                out8[7] = 255;
            }
        }

        private static void DecodeColorRamp(ushort c0, ushort c1, byte[] r, byte[] g, byte[] b, byte[] a)
        {
            Rgb565(c0, out r[0], out g[0], out b[0]);
            Rgb565(c1, out r[1], out g[1], out b[1]);
            a[0] = 255;
            a[1] = 255;
            if (c0 > c1)
            {
                r[2] = (byte)((2 * r[0] + r[1]) / 3);
                g[2] = (byte)((2 * g[0] + g[1]) / 3);
                b[2] = (byte)((2 * b[0] + b[1]) / 3);
                r[3] = (byte)((r[0] + 2 * r[1]) / 3);
                g[3] = (byte)((g[0] + 2 * g[1]) / 3);
                b[3] = (byte)((b[0] + 2 * b[1]) / 3);
                a[2] = 255;
                a[3] = 255;
            }
            else
            {
                r[2] = (byte)((r[0] + r[1]) / 2);
                g[2] = (byte)((g[0] + g[1]) / 2);
                b[2] = (byte)((b[0] + b[1]) / 2);
                r[3] = g[3] = b[3] = 0;
                a[2] = 255;
                a[3] = 0;
            }
        }

        private static void Rgb565(ushort c, out byte r, out byte g, out byte b)
        {
            var rr = (c >> 11) & 0x1F;
            var gg = (c >> 5) & 0x3F;
            var bb = c & 0x1F;
            r = (byte)((rr << 3) | (rr >> 2));
            g = (byte)((gg << 2) | (gg >> 4));
            b = (byte)((bb << 3) | (bb >> 2));
        }

        // ---------------- PNG ----------------

        /// <summary>RGBA32 → PNG（filter=0，zlib 用 ZLibStream）。</summary>
        public static byte[] EncodePng(byte[] rgba, int w, int h)
        {
            LastError = null;
            if (rgba == null || rgba.Length < w * h * 4) { LastError = "RGBA 数据不足"; return null; }

            try
            {
                using var ms = new MemoryStream();
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var ihdr = new byte[13];
                WriteBe(ihdr, 0, w);
                WriteBe(ihdr, 4, h);
                ihdr[8] = 8;    // bit depth
                ihdr[9] = 6;    // color type RGBA
                ihdr[10] = 0;   // deflate
                ihdr[11] = 0;   // filter
                ihdr[12] = 0;   // no interlace
                WriteChunk(ms, "IHDR", ihdr);

                // 原始扫描线：每行前面加一个 filter 字节 0
                var raw = new byte[h * (1 + w * 4)];
                for (var row = 0; row < h; row++)
                {
                    raw[row * (1 + w * 4)] = 0;
                    Buffer.BlockCopy(rgba, row * w * 4, raw, row * (1 + w * 4) + 1, w * 4);
                }

                byte[] zlib;
                using (var zms = new MemoryStream())
                {
                    using (var z = new ZLibStream(zms, CompressionLevel.Fastest, true))
                    {
                        z.Write(raw, 0, raw.Length);
                    }

                    zlib = zms.ToArray();
                }

                WriteChunk(ms, "IDAT", zlib);
                WriteChunk(ms, "IEND", Array.Empty<byte>());
                return ms.ToArray();
            }
            catch (Exception e)
            {
                LastError = "PNG 编码失败: " + e.GetType().Name + " " + e.Message;
                return null;
            }
        }

        private static void WriteBe(byte[] buf, int off, int v)
        {
            buf[off] = (byte)(v >> 24);
            buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8);
            buf[off + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBe(len, 0, data.Length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4];
            for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            var crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteBe(crcBytes, 0, unchecked((int)crc));
            s.Write(crcBytes, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }

            return t;
        }

        private static uint Crc32(byte[] a, byte[] b)
        {
            var c = 0xFFFFFFFFu;
            foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
