"""从 APK 里取出 libunity.so / libil2cpp.so 并做 ELF 符号/字符串扫描。

用法：
  python elfscan.py <apk> [--extract 输出目录] [--grep 关键字 ...]

关注点（Lead 指定的方向）：裁剪版引擎砍掉的 ICall 注册表条目，
其原生实现函数是否仍在 libunity.so 的导出/符号表里。
"""
import argparse
import os
import re
import struct
import sys
import zipfile

PATTERNS = [
    "CreateSprite", "Sprite::", "Sprite_", "GetSprite", "Internal_CreateSprite",
    "CreateSpriteWithoutTextureScripting", "LoadRawTextureData", "Texture2D::",
    "AudioClip", "Construct_Internal", "CreateUserSound",
    "il2cpp_add_internal_call", "il2cpp_resolve_icall",
]


def parse_elf_sections(data):
    if data[:4] != b"\x7fELF":
        return None
    is64 = data[4] == 2
    little = data[5] == 1
    if not (is64 and little):
        return None
    e_shoff, = struct.unpack_from("<Q", data, 0x28)
    e_shentsize, e_shnum, e_shstrndx = struct.unpack_from("<HHH", data, 0x3A)
    secs = []
    for i in range(e_shnum):
        off = e_shoff + i * e_shentsize
        if off + 64 > len(data):
            break
        sh_name, sh_type = struct.unpack_from("<II", data, off)
        sh_flags, sh_addr, sh_offset, sh_size = struct.unpack_from("<QQQQ", data, off + 8)
        sh_link, sh_info = struct.unpack_from("<II", data, off + 40)
        sh_addralign, sh_entsize = struct.unpack_from("<QQ", data, off + 48)
        secs.append(dict(name_off=sh_name, type=sh_type, flags=sh_flags, addr=sh_addr,
                         offset=sh_offset, size=sh_size, link=sh_link, info=sh_info,
                         entsize=sh_entsize))
    # section name string table
    names = b""
    if e_shstrndx < len(secs):
        s = secs[e_shstrndx]
        names = data[s["offset"]:s["offset"] + s["size"]]
    for s in secs:
        end = names.find(b"\0", s["name_off"])
        s["name"] = names[s["name_off"]:end].decode("utf-8", "replace") if end >= 0 else ""
    return secs


def read_strtab(data, sec):
    return data[sec["offset"]:sec["offset"] + sec["size"]]


def elf_symbols(data, secs, sym_type):
    out = []
    for sec in secs:
        if sec["type"] != sym_type:
            continue
        strtab = None
        if sec["link"] < len(secs):
            strtab = read_strtab(data, secs[sec["link"]])
        if strtab is None:
            continue
        entsize = sec["entsize"] or 24
        n = sec["size"] // entsize
        for i in range(n):
            off = sec["offset"] + i * entsize
            st_name, st_info, st_other, st_shndx = struct.unpack_from("<IBBH", data, off)
            st_value, st_size = struct.unpack_from("<QQ", data, off + 8)
            if st_name == 0:
                continue
            e = strtab.find(b"\0", st_name)
            nm = strtab[st_name:e].decode("utf-8", "replace")
            out.append((nm, st_value, st_size, st_shndx, st_info & 0xF))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("apk")
    ap.add_argument("--extract", default=None)
    ap.add_argument("--member", default="lib/arm64-v8a/libunity.so")
    args = ap.parse_args()

    with zipfile.ZipFile(args.apk) as z:
        libs = [n for n in z.namelist() if n.startswith("lib/")]
        print("== APK 内 native 库 ==")
        for n in libs:
            print("   ", n, z.getinfo(n).file_size)

        target = args.member
        if target not in libs:
            print("!! 找不到", target)
            return 1
        data = z.read(target)
        print("\n== %s : %.1f MB ==" % (target, len(data) / 1048576.0))
        if args.extract:
            os.makedirs(args.extract, exist_ok=True)
            p = os.path.join(args.extract, os.path.basename(target))
            with open(p, "wb") as f:
                f.write(data)
            print("已写出", p)

    secs = parse_elf_sections(data)
    if secs is None:
        print("!! 不是 64 位小端 ELF")
        return 1
    print("\n== 段表 ==")
    for s in secs:
        if s["name"] in (".dynsym", ".dynstr", ".symtab", ".strtab", ".rodata", ".text"):
            print("   %-10s type=%d size=%d entsize=%d" % (s["name"], s["type"], s["size"], s["entsize"]))

    dyn = elf_symbols(data, secs, 11)   # SHT_DYNSYM
    sym = elf_symbols(data, secs, 2)    # SHT_SYMTAB
    print("\n== 动态符号数 = %d ; 静态符号数 = %d (0 表示已 strip) ==" % (len(dyn), len(sym)))
    print("   导出函数数(有地址、非 UND) = %d" % len([s for s in dyn if s[1] != 0 and s[3] != 0]))

    for label, table in (("dynsym", dyn), ("symtab", sym)):
        hits = []
        for nm, val, size, shndx, typ in table:
            for p in PATTERNS:
                if p in nm:
                    hits.append((nm, val, size, shndx, typ))
                    break
        print("\n== %s 里匹配 Sprite/Texture/AudioClip/il2cpp 的符号 = %d ==" % (label, len(hits)))
        for nm, val, size, shndx, typ in hits[:200]:
            print("   %-70s 0x%x size=%d shndx=%d type=%d" % (nm[:70], val, size, shndx, typ))

    print("\n== 全文件字符串扫描（ICall 名字是否还在二进制里）==")
    for p in PATTERNS:
        idx = []
        start = 0
        b = p.encode()
        while True:
            i = data.find(b, start)
            if i < 0:
                break
            idx.append(i)
            start = i + 1
            if len(idx) >= 6:
                break
        print("   %-30s 出现 %s 次 %s" % (p, len(idx) if len(idx) < 6 else ">=6",
                                          ["0x%x" % i for i in idx]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
