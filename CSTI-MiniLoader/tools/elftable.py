"""深入看 libunity.so 里 ICall 名字字符串附近的布局：
   - 打印全部动态符号（libunity 导入了哪些 il2cpp_* 函数）
   - 在每个 ICall 名字字符串附近找「落在 .text 范围内的 8 字节值」（疑似函数指针表 / 重定位 addend）
   - 解析 .rela.dyn，看有没有重定位指向这些位置（PIC 下函数指针表靠重定位填）
"""
import struct
import sys

sys.path.insert(0, r"D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\tools")
from elfscan import parse_elf_sections, elf_symbols  # noqa: E402

PATH = sys.argv[1] if len(sys.argv) > 1 else r"D:\RiderProjects\ml-installer-06\clone_stage\stock_signed.apk"
MEMBER = sys.argv[2] if len(sys.argv) > 2 else "lib/arm64-v8a/libunity.so"
if PATH.lower().endswith(".apk"):
    import zipfile
    with zipfile.ZipFile(PATH) as z:
        data = z.read(MEMBER)
    print("== 从 %s 读取 %s (%d 字节) ==" % (PATH, MEMBER, len(data)))
else:
    data = open(PATH, "rb").read()
secs = parse_elf_sections(data)
by = {s["name"]: s for s in secs}

print("== 段 ==")
for n in (".text", ".rodata", ".data", ".data.rel.ro", ".rela.dyn", ".dynsym", ".dynstr", ".got", ".bss"):
    s = by.get(n)
    if s:
        print("   %-12s off=0x%-9x addr=0x%-9x size=0x%x" % (n, s["offset"], s["addr"], s["size"]))

dyn = elf_symbols(data, secs, 11)
print("\n== 动态符号 %d 个（前 60）==" % len(dyn))
for nm, val, size, shndx, typ in dyn[:60]:
    print("   %-60s val=0x%x shndx=%d type=%d" % (nm[:60], val, shndx, typ))

text = by.get(".text")
tlo, thi = (text["addr"], text["addr"] + text["size"]) if text else (0, 0)


def file_off_of_rodata(off):
    return off


TARGETS = ["UnityEngine.Sprite::CreateSprite", "UnityEngine.Sprite::Create",
           "UnityEngine.Sprite::Internal_CreateSprite", "UnityEngine.Texture2D::LoadRawTextureData",
           "UnityEngine.AudioClip::Construct_Internal"]
print("\n== 名字字符串附近的 8 字节值（是否落在 .text 0x%x-0x%x）==" % (tlo, thi))
for t in TARGETS:
    b = t.encode()
    i = data.find(b)
    if i < 0:
        print("   %-46s 未找到" % t)
        continue
    print("   %-46s 文件偏移=0x%x" % (t, i))
    for delta in range(-40, 56, 8):
        j = i + delta
        if j < 0 or j + 8 > len(data):
            continue
        v, = struct.unpack_from("<Q", data, j)
        mark = ""
        if tlo <= v < thi:
            mark = "  <== 落在 .text"
        elif 0x1000 < v < 0x100000000:
            mark = "  (小值)"
        if mark or delta == 0:
            print("      +%4d: 0x%016x%s" % (delta, v, mark))
    # 也看字符串结束后的对齐区
    end = i + len(b)
    print("      end=%d 后续 32 字节: %s" % (end, data[end:end + 32].hex()))

rela = by.get(".rela.dyn")
if rela:
    n = rela["size"] // 24
    print("\n== .rela.dyn 共 %d 条，找上表字符串附近的槽位 ==" % n)
    slots = {}
    for t in TARGETS:
        i = data.find(t.encode())
        if i >= 0:
            slots[i] = t
    cnt = 0
    for k in range(n):
        off = rela["offset"] + k * 24
        r_offset, r_info, r_addend = struct.unpack_from("<QQq", data, off)
        if text and tlo <= r_addend < thi:
            cnt += 1
            if cnt <= 25:
                print("   slot=0x%x type=%d addend=0x%x" % (r_offset, r_info & 0xFFFFFFFF, r_addend))
    print("   addend 落在 .text 的重定位共 %d 条（只列前 25）" % cnt)
