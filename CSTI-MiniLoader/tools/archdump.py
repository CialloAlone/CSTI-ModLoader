"""离线解包 Windy.modArch_V3 —— 不碰设备、不改 mod 源码，只做只读事实汇总。

实现的是 CSTI-MiniLoader 的容器格式：
  · .NET BinaryReader 字符串 = 7bit 变长长度 + UTF-8
  · 各区块 = blkCount(int32) + 每块[lz4Len(int32), blkLen(int32), LZ4 块压缩数据]
  · JsonsBLK 内层 = 循环{ itemFlg(int32); 0=结束; 2=StringMapper 表; 否则 listStr + MapperItem }
  · MapperItem = 1 字节类型: 1=对象(计数+[key int32, item]*) 2=数组(计数+item*) 3=bool(1B)
                 4=double(8B) 5=int(4B) 6=long(8B) 7=string(int32 索引)
"""
import collections
import io
import struct
import sys

PATH = r"D:\RiderProjects\ml-installer-06\clone_stage\Mods\Windy.modArch_V3"
END = "_End_"


# ---------------- LZ4 块解压 ----------------
def lz4_decompress(src, dst_size):
    dst = bytearray()
    i = 0
    n = len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        mlen = token & 0x0F
        if mlen == 15:
            while True:
                b = src[i]; i += 1
                mlen += b
                if b != 255:
                    break
        mlen += 4
        start = len(dst) - off
        if start < 0:
            raise ValueError("lz4: bad offset")
        for k in range(mlen):
            dst.append(dst[start + k])
    if dst_size and len(dst) != dst_size:
        pass
    return bytes(dst)


class R:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def i32(self):
        v, = struct.unpack_from("<i", self.d, self.p)
        self.p += 4
        return v

    def i64(self):
        v, = struct.unpack_from("<q", self.d, self.p)
        self.p += 8
        return v

    def byte(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def f64(self):
        v, = struct.unpack_from("<d", self.d, self.p)
        self.p += 8
        return v

    def s(self):
        # .NET 7bit 变长长度
        ln = 0
        shift = 0
        while True:
            b = self.d[self.p]
            self.p += 1
            ln |= (b & 0x7F) << shift
            shift += 7
            if (b & 0x80) == 0:
                break
        out = self.d[self.p:self.p + ln].decode("utf-8", "replace")
        self.p += ln
        return out

    def liststr(self):
        c = self.i32()
        return [self.s() for _ in range(c)]

    def mapper_item(self, depth=0):
        t = self.byte()
        if depth > 64:
            raise ValueError("mapper 太深")
        if t == 1:
            c = self.i32()
            for _ in range(c):
                self.i32()
                self.mapper_item(depth + 1)
        elif t == 2:
            c = self.i32()
            for _ in range(c):
                self.mapper_item(depth + 1)
        elif t == 3:
            self.byte()
        elif t == 4:
            self.f64()
        elif t == 5:
            self.i32()
        elif t == 6:
            self.i64()
        elif t == 7:
            self.i32()
        else:
            raise ValueError("未知 MapperItem 类型 " + str(t))


def read_mapper_item(r, mapper, depth=0):
    """按 MapperItem 编码还原成 Python 对象（对应 mod 的 MapperItem.Read）。"""
    t = r.byte()
    if depth > 64:
        raise ValueError("mapper 太深")
    if t == 1:                      # MapperObject
        c = r.i32()
        d = {}
        for _ in range(c):
            k = r.i32()
            key = mapper[k] if 0 <= k < len(mapper) else ("#%d" % k)
            d[key] = read_mapper_item(r, mapper, depth + 1)
        return d
    if t == 2:                      # MapperList
        c = r.i32()
        return [read_mapper_item(r, mapper, depth + 1) for _ in range(c)]
    if t == 3:
        return bool(r.byte())
    if t == 4:
        return r.f64()
    if t == 5:
        return r.i32()
    if t == 6:
        return r.i64()
    if t == 7:                      # ObjString → 字符串表索引
        k = r.i32()
        return mapper[k] if 0 <= k < len(mapper) else None
    raise ValueError("未知 MapperItem 类型 " + str(t))


def decode_jsons(r):
    """解出 JsonsBLK 每个条目的 (listStr, JSON)。"""
    out = []
    cnt = r.i32()
    for _ in range(cnt):
        lz4len = r.i32()
        blklen = r.i32()
        raw = r.d[r.p:r.p + lz4len]
        r.p += lz4len
        buf = lz4_decompress(raw, blklen)
        rr = R(buf)
        mapper = []
        while True:
            flg = rr.i32()
            if flg == 0:
                break
            if flg == 2:
                c = rr.i32()
                mapper = [rr.s() for _ in range(c)]
                continue
            ls = rr.liststr()
            out.append((ls, read_mapper_item(rr, mapper)))
    return out


def analyze_gsm():
    import collections
    import json as _json

    data = open(PATH, "rb").read()
    r = R(data)
    r.s()
    js = None
    while True:
        name = r.s()
        if name == END:
            break
        if name == "JsonsBLK":
            js = decode_jsons(r)
        else:
            handler(name, r)
    if js is None:
        print("没有 JsonsBLK")
        return

    gsm = [(ls, o) for ls, o in js if ls and ls[0] == "GameSourceModify"]
    print("\n############ GameSourceModify 条目数 = %d ############" % len(gsm))

    # 目标 GUID → 是否 mod 自己的对象（用其他 JSON 条目的 UniqueID 反查）
    mod_ids = {}
    for ls0, o0 in js:
        if isinstance(o0, dict) and isinstance(o0.get("UniqueID"), str):
            mod_ids[o0["UniqueID"]] = (ls0[0] if ls0 else "?")

    print("\n== 分类（路径第 2 段）统计 ==")
    cats = collections.Counter()
    for ls, o in gsm:
        parts = "/".join(ls).split("/")
        cats[parts[1] if len(parts) > 1 else "?"] += 1
    for k, v in cats.most_common():
        print("   %-26s %d" % (k, v))

    print("\n== 每条：分类 / 目标GUID / 目标归属 / 被改字段 / WarpType / 元素数 ==")
    for ls, o in gsm:
        parts = "/".join(ls).split("/")
        cat = parts[1] if len(parts) > 1 else "?"
        guid = parts[-1].replace(".json", "")[:32]
        owner = mod_ids.get(guid, "**原版对象**")
        for k, v in o.items():
            if not k.endswith("WarpType"):
                continue
            fld = k[:-8]
            data = o.get(fld + "WarpData")
            n = len(data) if isinstance(data, list) else 1
            print("%-14s %-34s %-14s %-22s type=%-2s 元素=%d"
                  % (cat, guid, owner, fld, v, n))

    print("\n== 字段并集统计 ==")
    cnt = collections.Counter()
    for ls, o in gsm:
        for k in o.keys():
            cnt[k] += 1
    for k, v in cnt.most_common(40):
        print("   %-46s %d" % (k, v))

    print("\n== 天气 / windy / locat / env 条目细节 ==")
    for ls, o in gsm:
        path = "/".join(ls)
        cat = path.split("/")[1] if len(path.split("/")) > 1 else "?"
        if cat not in ("weather", "windy", "locat", "env"):
            continue
        guid = path.split("/")[-1].replace(".json", "")[:32]
        print("\n--- %s  目标=%s (%s) ---" % (path, guid, mod_ids.get(guid, "**原版对象**")))
        for k, v in o.items():
            if k.endswith("WarpData") and isinstance(v, list):
                print("   %s : %d 个元素" % (k, len(v)))
                for idx, el in enumerate(v[:3]):
                    if not isinstance(el, dict):
                        continue
                    flat = {}
                    for kk, vv in el.items():
                        if isinstance(vv, (list, dict)):
                            continue
                        flat[kk] = vv
                    print("      [%d] %s" % (idx, _json.dumps(flat, ensure_ascii=False)[:500]))
                    for kk, vv in el.items():
                        sv = _json.dumps(vv, ensure_ascii=False)
                        if sv in ("[]", "{}", '""', "null", "false", "0"):
                            continue
                        if "Warp" in kk or isinstance(vv, (list, dict)):
                            print("          %-34s = %s" % (kk, sv[:190]))
            else:
                print("   %-36s = %s" % (k, _json.dumps(v, ensure_ascii=False)[:200]))
            print("\n--- %s（目标=%s，字段 %d）---" % ("/".join(ls), o.get("UniqueID", "?"), len(o)))
            for k, v in o.items():
                sv = _json.dumps(v, ensure_ascii=False)
                print("   %-38s = %s" % (k, sv if len(sv) < 220 else sv[:220] + "…"))


def blocks(data):
    r = R(data)
    mod = r.s()
    print("== modName =", mod)
    seq = []
    while True:
        name = r.s()
        if name == END:
            break
        blk_start = r.p
        seq.append((name, blk_start))
        handler(name, r)
    return seq


def subblocks(r, fn):
    cnt = r.i32()
    out = []
    for _ in range(cnt):
        lz4len = r.i32()
        blklen = r.i32()
        raw = r.d[r.p:r.p + lz4len]
        r.p += lz4len
        out.append(fn(lz4_decompress(raw, blklen)))
    return out


def handler(name, r):
    print("\n===== 区块 %s =====" % name)
    if name == "LuaBLK":
        all_lua = []
        for buf in subblocks(r, lambda b: b):
            rr = R(buf)
            while True:
                flg = rr.i32()
                if flg == 0:
                    break
                ls = rr.liststr()
                lua = rr.s()
                all_lua.append((ls, lua))
        print("Lua 条目数 =", len(all_lua))
        names = collections.Counter(x[0][0] if x[0] else "?" for x in all_lua)
        print("按第一段分组:", dict(names))
        for ls, lua in all_lua[:12]:
            first = lua.strip().splitlines()[0] if lua.strip() else ""
            print("   %-60s 行数=%-5d 首行=%s" % ("/".join(ls)[:60], lua.count("\n") + 1, first[:90]))
    elif name == "LocalBLK":
        pairs = []
        for buf in subblocks(r, lambda b: b):
            rr = R(buf)
            while True:
                nm = rr.s()
                if nm == END:
                    break
                pairs.append((nm, rr.s()))
        print("本地化条目数 =", len(pairs))
        for nm, content in pairs[:10]:
            print("   %-40s 字符数=%-8d 首行=%s" % (nm, len(content),
                                                    content.strip().splitlines()[0][:90] if content.strip() else ""))
    elif name == "AudioBLK":
        # 内层格式另说：这里只读区块头，统计"块数 / 每块原始大小"
        cnt = r.i32()
        sizes = []
        for _ in range(cnt):
            lz4len = r.i32()
            blklen = r.i32()
            r.p += lz4len
            sizes.append(blklen)
        print("音频子块数 =", cnt, " 各块解码后大小 =", sizes)
    elif name == "ImgBLK":
        total_png = total_atlas = 0
        for buf in subblocks(r, lambda b: b):
            rr = R(buf)
            while True:
                flg = rr.i32()
                if flg == 0:
                    break
                if flg == 1:
                    nm = rr.s()
                    w = rr.i32(); h = rr.i32(); fmt = rr.i32(); ln = rr.i32()
                    rr.p += ln
                    total_png += 1
                    print("   单图 %-40s %dx%d fmt=%d %d 字节" % (nm[-40:], w, h, fmt, ln))
                elif flg == 2:
                    rects = []
                    c = rr.i32()
                    for _ in range(c):
                        w = struct.unpack_from("<f", rr.d, rr.p)[0]; rr.p += 4
                        h = struct.unpack_from("<f", rr.d, rr.p)[0]; rr.p += 4
                        x = struct.unpack_from("<f", rr.d, rr.p)[0]; rr.p += 4
                        y = struct.unpack_from("<f", rr.d, rr.p)[0]; rr.p += 4
                        rects.append((x, y, w, h))
                    tex = rr.s()
                    lst = rr.liststr()
                    fmt = rr.i32(); tw = rr.i32(); th = rr.i32(); ln = rr.i32()
                    rr.p += ln
                    total_atlas += 1
                    print("   图集 %-40s %dx%d fmt=%d 精灵=%d %d 字节" % (tex[-40:], tw, th, fmt, len(lst), ln))
                    for nm, rc in list(zip(lst, rects))[:4]:
                        print("        %-40s rect=%s" % (nm[-40:], rc))
        print("图片块汇总: 单图=%d 图集=%d" % (total_png, total_atlas))
    elif name == "JsonsBLK":
        kinds = collections.Counter()
        top = collections.Counter()
        names_by_kind = collections.defaultdict(list)
        for buf in subblocks(r, lambda b: b):
            rr = R(buf)
            mapper = []
            while True:
                flg = rr.i32()
                if flg == 0:
                    break
                if flg == 2:
                    c = rr.i32()
                    for _ in range(c):
                        rr.s()
                    continue
                ls = rr.liststr()
                rr.mapper_item()
                key0 = ls[0] if ls else "?"
                kinds[key0] += 1
                if key0 not in ("ScriptableObject", "GameSourceModify") and ls:
                    top[ls[0]] += 1
                if len(names_by_kind[key0]) < 3 and ls:
                    names_by_kind[key0].append(ls[-1][-60:] if ls[-1] else "?")
        print("JsonsBLK 条目分类（listStr[0]）:")
        for k, v in kinds.most_common():
            print("   %-28s %d   例: %s" % (k, v, ", ".join(names_by_kind[k])))
    else:
        print("   (未实现解析，跳过)")


if "--gsm" in sys.argv:
    analyze_gsm()
else:
    data = open(PATH, "rb").read()
    print("文件大小 = %.1f MB" % (len(data) / 1048576.0))
    seq = blocks(data)
    print("\n== 区块顺序 ==")
    print(" → ".join(n for n, _ in seq))
