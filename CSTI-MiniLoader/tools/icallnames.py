"""把 libunity.so 里所有 ICall 名字字符串抽出来，和 icall_missing_real.txt 对照。

背景：Sprite 的坑已经证明——managed 侧解析的名字被裁了，但引擎实际注册的名字
（如 UnityEngine.Sprite::CreateSprite_Injected）仍以字符串形式留在 .rodata 里。
所以「引擎里到底有哪些 ICall 名字」可以直接从二进制里枚举出来，
不用再靠猜 _Injected/Impl 变体。
"""
import re
import sys
import zipfile

APK = r"D:\RiderProjects\ml-installer-06\clone_stage\stock_signed.apk"
MEMBER = "lib/arm64-v8a/libunity.so"
MISSING = r"D:\RiderProjects\ml-installer-06\icall_missing_real.txt"

NAME_RE = re.compile(rb"^(?:UnityEngine|Unity|System|Microsoft)\.[A-Za-z0-9_.+`<>\[\]]*::[A-Za-z0-9_<>`]+$")

with zipfile.ZipFile(APK) as z:
    data = z.read(MEMBER)

# 扫 printable 串
found = set()
i = 0
n = len(data)
while True:
    j = data.find(b"\x00", i)
    if j < 0:
        break
    if 12 < j - i < 200:
        s = data[i:j]
        if b"::" in s and all(32 <= c < 127 for c in s):
            if NAME_RE.match(s):
                found.add(s.decode())
    i = j + 1

print("== libunity.so 里形如 X::Y 的名字共 %d 个 ==" % len(found))

missing = []
with open(MISSING, encoding="utf-8", errors="replace") as f:
    for line in f:
        s = line.strip()
        if "::" in s:
            missing.append(s)

print("== 缺失清单 %d 个 ==" % len(missing))

def base(n):
    return n.split("::")[1]

print("\n== 缺失名 ↔ 引擎实际名字 对照（按方法名匹配）==")
idx = {}
for n in found:
    idx.setdefault(base(n), []).append(n)

hit_same, hit_renamed, absent = [], [], []
for m in missing:
    if m in found:
        hit_same.append(m)
        continue
    cands = [c for c in idx.get(base(m), []) if c != m]
    if cands:
        hit_renamed.append((m, cands))
    else:
        absent.append(m)

print("\n-- 名字完全一致（本来就该能解析，%d）--" % len(hit_same))
for m in hit_same:
    print("   ", m)

print("\n-- 名字对不上但有同方法名的候选（%d）--" % len(hit_renamed))
for m, cs in hit_renamed:
    print("   %-58s -> %s" % (m, ", ".join(cs[:4])))

print("\n-- 引擎里完全没有同名方法（%d）--" % len(absent))
for m in absent:
    print("   ", m)

print("\n== 顺带：libunity.so 里所有 Audio / Sprite / Texture 相关注册名 ==")
for n in sorted(found):
    if any(k in n for k in ("Audio", "Sprite", "Texture", "ImageConversion", "Mesh", "Font")):
        print("   ", n)
