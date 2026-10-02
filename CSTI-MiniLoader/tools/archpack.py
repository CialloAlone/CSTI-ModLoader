"""archpack.py —— 把「PC 格式的作者 mod 目录」打包成 `.modArch_V3`（Android MiniLoader 可读）

格式依据（PC 侧导出器的**写出**实现，逐行对照）：
  D:\\RiderProjects\\CSTI-ModLoader\\CSTI-ModLoader\\ExportUtil\\ExportAll.cs
      · CollectJson  : 枚举 mod 根目录下所有文件 → .json/.jsonnet 进 JsonsBLK、.csv 进 LocalBLK、.lua 进 LuaBLK
      · CollectImgV2 : Resource/Picture 下 png/jpg/jpeg；单图 itemFlg=1 → 文件名,w,h,format,dataLen,data
      · CollectAudio : Resource/Audio 下 wav/mp3/ogg → 文件名,dataLen,data
      · Export()     : 容器 = ModName + [区块名, 块数, 每块(len,rawLen,data)] + "_End_"
  D:\\RiderProjects\\CSTI-ModLoader\\CSTI-ModLoader\\ExportUtil\\MapperObject.cs
      · MapperItem.Write: 1=对象(计数 + [键字符串索引, 值]*)、2=数组(计数 + 值*)、3=bool(1B)、
                          4=double(8B)、5=int(4B)、6=long(8B)、7=字符串(4B 字符串表索引)
      · CreateByJson 判定顺序: IsObject → IsArray → IsLong → IsInt → IsBoolean → IsString → IsDouble

用法：
    python archpack.py <mod目录> [-o 输出.modArch_V3] [--rgba _Cart_338x512.rgba]

注意：JPEG/PNG 需要先解码成 RGBA32 再打包（本脚本不依赖 PIL）。
     在 Windows 上可用 PowerShell 的 System.Drawing 解码：
       LockBits(Format32bppArgb) → 逐像素 BGRA→RGBA → 写成 _<名>_<w>x<h>.rgba
     然后 --rgba 指向该文件（文件名里带 w/h，脚本会自动取尺寸）。
"""
import json
import os
import struct
import sys

END = "_End_"
BLOCKS = ("ImgBLK", "JsonsBLK", "LocalBLK", "AudioBLK", "LuaBLK")
RGBA32 = 4
DXT5 = 12


# ───────────────────────── .NET BinaryWriter 兼容写入 ─────────────────────────
def wstr(buf, s):
    """BinaryWriter.Write(string)：7bit 变长字节长 + UTF-8（无 BOM）。"""
    data = s.encode("utf-8")
    n = len(data)
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            buf.append(b | 0x80)
        else:
            buf.append(b)
            break
    buf += data


def wi32(buf, v):
    buf += struct.pack("<i", v)


def wf64(buf, v):
    buf += struct.pack("<d", v)


def wliststr(buf, items):
    """RWUtil.Write(this BinaryWriter, List<string>)：count + 每项字符串。"""
    wi32(buf, len(items))
    for s in items:
        wstr(buf, s)


# ───────────────────────── LZ4（literal-only 合法块） ─────────────────────────
def lz4_literal(data):
    """只由 literal 组成的合法 LZ4 block —— 解码器读完 literal 后输入即结束（K4os/自研解码器都接受）。"""
    out = bytearray()
    i = 0
    n = len(data)
    if n == 0:
        return bytes([0])
    while i < n:
        ln = min(n - i, 0x7FFFFF00)
        out.append((15 if ln >= 15 else ln) << 4)
        if ln >= 15:
            rest = ln - 15
            while rest >= 255:
                out.append(255)
                rest -= 255
            out.append(rest)
        out += data[i:i + ln]
        i += ln
    return bytes(out)


def make_block(inner: bytes):
    comp = lz4_literal(inner)
    return (len(comp), len(inner), comp)


# ───────────────────────── JSON → MapperItem ─────────────────────────
class StringMapper:
    def __init__(self):
        self.list = []
        self.idx = {}

    def add(self, s):
        if s not in self.idx:
            self.idx[s] = len(self.list)
            self.list.append(s)

    def collect(self, o):
        """与 StringMapper.CollectJson 同序：对象 → 先加键名，字符串值直接加，其余递归；数组 → 字符串加，其余递归。"""
        if isinstance(o, dict):
            for k, v in o.items():
                self.add(k)
                if isinstance(v, str):
                    self.add(v)
                else:
                    self.collect(v)
        elif isinstance(o, list):
            for v in o:
                if isinstance(v, str):
                    self.add(v)
                else:
                    self.collect(v)


def write_item(buf, o, m: StringMapper):
    if isinstance(o, dict):
        buf.append(1)
        wi32(buf, len(o))
        for k, v in o.items():
            wi32(buf, m.idx[k])
            write_item(buf, v, m)
    elif isinstance(o, list):
        buf.append(2)
        wi32(buf, len(o))
        for v in o:
            write_item(buf, v, m)
    elif isinstance(o, bool):
        buf.append(3)
        buf.append(1 if o else 0)
    elif isinstance(o, float):
        buf.append(4)
        wf64(buf, o)
    elif isinstance(o, int):
        if -2 ** 31 <= o < 2 ** 31:
            buf.append(5)
            wi32(buf, o)
        else:
            buf.append(6)
            buf += struct.pack("<q", o)
    elif isinstance(o, str):
        buf.append(7)
        wi32(buf, m.idx[o])
    else:
        raise ValueError("不支持的 JSON 类型: " + repr(type(o)))


# ───────────────────────── 打包 ─────────────────────────
def pack(mod_dir: str, out_path: str, rgba_path: str = None, verbose=True, save=True):
    mod_dir = os.path.abspath(mod_dir)
    mod_name = os.path.basename(mod_dir)
    rgba = None
    rgba_w = rgba_h = 0
    if rgba_path and os.path.exists(rgba_path):
        rgba = open(rgba_path, "rb").read()
        base = os.path.basename(rgba_path)          # _Cart_338x512.rgba
        wh = base.rsplit(".", 1)[0].rsplit("_", 1)[-1]
        rgba_w, rgba_h = (int(x) for x in wh.lower().split("x"))
        assert len(rgba) == rgba_w * rgba_h * 4, "RGBA 体积与 w*h*4 不符"

    jsons, locals_, audios, lua = [], [], [], []
    images = []
    for root, dirs, files in os.walk(mod_dir):
        dirs.sort()
        for fn in sorted(files):
            full = os.path.join(root, fn)
            ext = os.path.splitext(fn)[1].lower()
            rel = os.path.relpath(full, mod_dir).replace("\\", "/")
            parts = rel.split("/")
            if ext in (".json", ".jsonnet"):
                with open(full, "r", encoding="utf-8-sig") as f:
                    jsons.append((parts, json.load(f)))
            elif ext == ".csv":
                with open(full, "r", encoding="utf-8-sig") as f:
                    locals_.append((fn, f.read()))
            elif ext == ".lua":
                with open(full, "r", encoding="utf-8-sig") as f:
                    lua.append((parts, f.read()))
            elif ext in (".wav", ".mp3", ".ogg"):
                audios.append((fn, open(full, "rb").read()))
            elif ext in (".png", ".jpg", ".jpeg"):
                images.append(fn)

    # ---- JsonsBLK ----
    mapper = StringMapper()
    for _, o in jsons:
        mapper.collect(o)
    js = bytearray()
    wi32(js, 2)
    wi32(js, len(mapper.list))
    for s in mapper.list:
        wstr(js, s)
    for parts, o in jsons:
        wi32(js, 1)
        wliststr(js, parts)
        write_item(js, o, mapper)
    wi32(js, 0)

    # ---- ImgBLK ----
    img = bytearray()
    for fn in images:
        if rgba is None:
            raise SystemExit("需要 --rgba 提供解码后的 RGBA32 数据（JPEG/PNG 不能直接进包）")
        wi32(img, 1)
        wstr(img, fn)
        wi32(img, rgba_w)
        wi32(img, rgba_h)
        wi32(img, RGBA32)
        wi32(img, len(rgba))
        img += rgba
    wi32(img, 0)

    # ---- LocalBLK ----
    loc = bytearray()
    for fn, content in locals_:
        wstr(loc, fn)
        wstr(loc, content)
    wstr(loc, END)

    # ---- AudioBLK ----
    aud = bytearray()
    for fn, data in audios:
        wstr(aud, fn)
        wi32(aud, len(data))
        aud += data
    wstr(aud, END)

    # ---- LuaBLK（无脚本时与导出器一致：单个 int32 0）----
    lu = bytearray()
    if lua:
        for parts, content in lua:
            wi32(lu, 1)
            wliststr(lu, parts)
            wstr(lu, content)
    wi32(lu, 0)

    # ---- 容器 ----
    out = bytearray()
    wstr(out, mod_name)
    for name, inner in zip(BLOCKS, (img, js, loc, aud, lu)):
        blk = make_block(bytes(inner))
        wstr(out, name)
        wi32(out, 1)
        wi32(out, blk[0])
        wi32(out, blk[1])
        out += blk[2]
    wstr(out, END)
    if save:
        with open(out_path, "wb") as f:
            f.write(out)

    if verbose:
        print("打包完成: %s（%.1f KB）" % (out_path, len(out) / 1024.0))
        print("  ModName   = %s" % mod_name)
        print("  JsonsBLK  = %d 条；字符串表 %d 项（%s）" % (len(jsons), len(mapper.list),
              ", ".join("/".join(p) for p, _ in jsons)))
        print("  ImgBLK    = %d 张（%s → %dx%d RGBA32 %d B）" % (len(images), ",".join(images),
              rgba_w, rgba_h, len(rgba) if rgba else 0))
        print("  LocalBLK  = %d 条；AudioBLK = %d 条（%s）；LuaBLK = %d 条" %
              (len(locals_), len(audios), ", ".join(fn for fn, _ in audios), len(lua)))
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(1)
    src = sys.argv[1]
    out = None
    rgba = None
    if "-o" in sys.argv:
        out = sys.argv[sys.argv.index("-o") + 1]
    if "--rgba" in sys.argv:
        rgba = sys.argv[sys.argv.index("--rgba") + 1]
    b64 = "--b64" in sys.argv or out == "-"
    if out is None or out == "-":
        out = os.path.abspath(src.rstrip("\\/")) + ".modArch_V3"
    data = pack(src, out, rgba, verbose=not b64, save=not b64)
    if b64:
        # 沙箱里 Python 不能写文件：用 base64 走 stdout，由 PowerShell 落盘
        import base64
        sys.stdout.write(base64.b64encode(data).decode("ascii"))
    else:
        try:
            with open(out, "wb") as f:
                f.write(data)
        except PermissionError:
            print("✗ 无写权限，请改用 --b64，由 PowerShell 落盘", file=sys.stderr)
            raise SystemExit(2)
