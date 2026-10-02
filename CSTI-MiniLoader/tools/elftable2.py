"""关键判定：libunity.so 里那些 ICall 名字字符串，是否仍被「{名字指针, 函数指针} 表」引用。
做法：
  1. 解析 .rela.dyn（R_AARCH64_RELATIVE，type=1027，addend 就是目标地址）
  2. 找 addend 落在 .rodata 且指向某个 ICall 名字字符串的槽位
  3. 看该槽位 ±8 字节有没有另一条 addend 落在 .text 的重定位（那就是函数指针）
输出：命中/未命中，以及候选 (字符串地址, 槽位, 邻槽, 函数地址)
"""
import struct
import sys
import zipfile

sys.path.insert(0, r"D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\tools")
from elfscan import parse_elf_sections  # noqa: E402

APK = r"D:\RiderProjects\ml-installer-06\clone_stage\stock_signed.apk"
MEMBER = "lib/arm64-v8a/libunity.so"
with zipfile.ZipFile(APK) as z:
    data = z.read(MEMBER)

secs = parse_elf_sections(data)
by = {s["name"]: s for s in secs}
text = by[".text"]
rodata = by[".rodata"]
rela = by[".rela.dyn"]
tlo, thi = text["addr"], text["addr"] + text["size"]
rlo, rhi = rodata["addr"], rodata["addr"] + rodata["size"]

# 收集重定位：slot(addr) -> addend
rel_by_slot = {}
rela_addends = {}
n = rela["size"] // 24
for k in range(n):
    off = rela["offset"] + k * 24
    r_offset, r_info, r_addend = struct.unpack_from("<QQq", data, off)
    typ = r_info & 0xFFFFFFFF
    rel_by_slot[r_offset] = (typ, r_addend)
    rela_addends.setdefault(r_addend, []).append(r_offset)
print("== .rela.dyn %d 条；addend 落在 .text 的 %d 条 ==" %
      (n, sum(1 for a in rela_addends if tlo <= a < thi)))

TARGETS = [
    "UnityEngine.Sprite::CreateSprite",
    "UnityEngine.Sprite::Create",
    "UnityEngine.Texture2D::LoadRawTextureData",
    "UnityEngine.AudioClip::Construct_Internal",
    "UnityEngine.AudioClip::CreateUserSound",
]


def cstr(addr):
    i = addr
    e = data.find(b"\0", i)
    return data[i:e].decode("utf-8", "replace")


for t in TARGETS:
    print("\n---- %s ----" % t)
    found_any = False
    start = 0
    while True:
        i = data.find(t.encode(), start)
        if i < 0:
            break
        start = i + 1
        full = cstr(i)                      # 直到 NUL 的完整名字
        # 往前找字符串起点（前一个 NUL 之后）
        b = data.rfind(b"\0", 0, i)
        full = data[b + 1:data.find(b"\0", i)].decode("utf-8", "replace")
        slots = rela_addends.get(i)
        if slots is None:
            # 也许引用的是字符串起点而不是匹配点
            slots = rela_addends.get(b + 1)
        ok = ""
        if slots:
            for s in slots:
                nb = []
                for d in (-16, -8, 8, 16):
                    e = rel_by_slot.get(s + d)
                    if e and tlo <= e[1] < thi:
                        nb.append("槽%+d=0x%x" % (d, e[1]))
                ok = "  ← 被表引用 slot=0x%x %s" % (s, " ".join(nb) if nb else "(邻槽无 .text 指针)")
                found_any = True
        print("   0x%-8x 完整名=%s%s" % (i, full[:70], ok))
        if not slots:
            print("            槽位=%s" % (slots,))
    if not found_any:
        print("   结论：没有任何重定位指向这些名字 → 引擎里已不存在 {名字,函数} 注册表")
