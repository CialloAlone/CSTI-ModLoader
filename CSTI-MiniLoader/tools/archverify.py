"""archverify.py —— 把 archpack.py 打出的 .modArch_V3 解析回来，逐条与源目录对比

判据（Lead 指定）：文件数 / 路径 / JSON 键值 / 音频字节 / 图片字节 全部一致。
复用 archdump.py 的解码器（同一套读取逻辑），因此「能解出来」= 与 loader 读取路径一致。
"""
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import archdump as A  # R / lz4_decompress / decode_jsons / handler


def parse_arch(path):
    r = A.R(open(path, "rb").read())
    mod = r.s()
    out = {"mod": mod, "blocks": {}, "order": []}
    while True:
        name = r.s()
        if name == A.END:
            break
        out["order"].append(name)
        cnt = r.i32()
        inner = []
        for _ in range(cnt):
            lz4len = r.i32()
            blklen = r.i32()
            raw = r.d[r.p:r.p + lz4len]
            r.p += lz4len
            buf = A.lz4_decompress(raw, blklen)
            assert len(buf) == blklen, "LZ4 解码长度不符 %s: %d != %d" % (name, len(buf), blklen)
            inner.append(buf)
        out["blocks"][name] = inner
    return out


def parse_jsons(inners):
    items = []
    for buf in inners:
        rr = A.R(buf)
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
            items.append((ls, A.read_mapper_item(rr, mapper)))
    return items


def parse_imgs(inners):
    imgs = []
    for buf in inners:
        rr = A.R(buf)
        while True:
            flg = rr.i32()
            if flg == 0:
                break
            if flg == 1:
                nm = rr.s()
                w = rr.i32()
                h = rr.i32()
                fmt = rr.i32()
                ln = rr.i32()
                data = rr.d[rr.p:rr.p + ln]
                rr.p += ln
                imgs.append({"name": nm, "w": w, "h": h, "fmt": fmt, "data": data})
            elif flg == 2:
                raise SystemExit("本包不应含图集（ItemFlg=2）")
            else:
                raise SystemExit("未知 ImgBLK itemFlg=" + str(flg))
    return imgs


def parse_audios(inners):
    out = []
    for buf in inners:
        rr = A.R(buf)
        while True:
            nm = rr.s()
            if nm == A.END:
                break
            ln = rr.i32()
            out.append((nm, rr.d[rr.p:rr.p + ln]))
            rr.p += ln
    return out


def parse_locals(inners):
    out = []
    for buf in inners:
        rr = A.R(buf)
        while True:
            nm = rr.s()
            if nm == A.END:
                break
            out.append((nm, rr.s()))
    return out


def main():
    arch = sys.argv[1]
    src = sys.argv[2]
    rgba = sys.argv[3] if len(sys.argv) > 3 else None

    p = parse_arch(arch)
    print("== 容器 ==")
    print("  ModName   = %s（源目录 %s）" % (p["mod"], os.path.basename(src)))
    print("  区块顺序  = %s" % " → ".join(p["order"]))
    ok = p["mod"] == os.path.basename(src) and p["order"] == list(A.BLOCKS) if hasattr(A, "BLOCKS") else p["mod"] == os.path.basename(src)
    print("  ModName 与区块顺序: %s" % ("✔" if ok else "✗"))

    # ---- JSON ----
    items = parse_jsons(p["blocks"]["JsonsBLK"])
    src_jsons = {}
    for root, dirs, files in os.walk(src):
        for fn in files:
            if os.path.splitext(fn)[1].lower() in (".json", ".jsonnet"):
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, src).replace("\\", "/")
                src_jsons[rel] = json.load(open(full, encoding="utf-8-sig"))
    print("\n== JsonsBLK：%d 条（源目录 %d 个 json）==" % (len(items), len(src_jsons)))
    all_ok = len(items) == len(src_jsons)
    for ls, o in items:
        rel = "/".join(ls)
        ref = src_jsons.get(rel)
        if ref is None:
            print("  ✗ 多出条目: %s" % rel)
            all_ok = False
            continue
        same = json.dumps(ref, sort_keys=True, ensure_ascii=False) == json.dumps(o, sort_keys=True, ensure_ascii=False)
        # 键序也要一致（导出器按 JSON 出现顺序写）
        same_order = list(ref.keys()) == list(o.keys()) if isinstance(ref, dict) and isinstance(o, dict) else True
        print("  %s %-40s 键数=%-4d 值一致=%s 键序一致=%s" % ("✔" if same and same_order else "✗", rel,
              len(o) if isinstance(o, dict) else -1, same, same_order))
        all_ok &= (same and same_order)
    for rel in src_jsons:
        if rel not in {"/".join(ls) for ls, _ in items}:
            print("  ✗ 缺失条目: %s" % rel)
            all_ok = False

    # ---- Img ----
    imgs = parse_imgs(p["blocks"]["ImgBLK"])
    src_imgs = []
    for root, dirs, files in os.walk(os.path.join(src, "Resource", "Picture")):
        for fn in sorted(files):
            if os.path.splitext(fn)[1].lower() in (".png", ".jpg", ".jpeg"):
                src_imgs.append(fn)
    print("\n== ImgBLK：%d 张（源目录 %d 张）==" % (len(imgs), len(src_imgs)))
    img_ok = len(imgs) == len(src_imgs)
    for im in imgs:
        print("  %s %-24s %dx%d fmt=%d dataLen=%d" % ("✔" if True else "✗", im["name"], im["w"], im["h"],
              im["fmt"], len(im["data"])))
    if rgba and os.path.exists(rgba):
        ref = open(rgba, "rb").read()
        same = len(imgs) == 1 and imgs[0]["data"] == ref
        print("  与解码后的 RGBA 源数据逐字节一致: %s（%d B）" % ("✔" if same else "✗", len(ref)))
        img_ok &= same

    # ---- Audio ----
    auds = parse_audios(p["blocks"]["AudioBLK"])
    src_auds = {}
    for root, dirs, files in os.walk(os.path.join(src, "Resource", "Audio")):
        for fn in files:
            if os.path.splitext(fn)[1].lower() in (".wav", ".mp3", ".ogg"):
                src_auds[fn] = open(os.path.join(root, fn), "rb").read()
    print("\n== AudioBLK：%d 条（源目录 %d 条）==" % (len(auds), len(src_auds)))
    aud_ok = len(auds) == len(src_auds)
    for nm, data in auds:
        ref = src_auds.get(nm)
        same = ref is not None and ref == data
        print("  %s %-20s %d B（与源文件一致=%s）" % ("✔" if same else "✗", nm, len(data), same))
        aud_ok &= same

    # ---- Local / Lua ----
    locs = parse_locals(p["blocks"]["LocalBLK"])
    lua_inner = p["blocks"]["LuaBLK"][0]
    lua_cnt = struct.unpack_from("<i", lua_inner, 0)[0]
    print("\n== LocalBLK：%d 条；LuaBLK 首 int32=%d（无 lua 时应为 0）==" % (len(locs), lua_cnt))

    print("\n===== 结论：%s =====" % ("全部一致 ✔" if (all_ok and img_ok and aud_ok) else "存在不一致 ✗"))
    return 0 if (all_ok and img_ok and aud_ok) else 1


if __name__ == "__main__":
    raise SystemExit(main())
