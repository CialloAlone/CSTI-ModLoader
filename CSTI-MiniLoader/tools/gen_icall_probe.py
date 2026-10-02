"""从 icall_missing_real.txt 生成 C# 探测名单 MissingIcalls.cs。

思路：裁剪版引擎可能只是「名字对不上」——比如 managed 侧解析 UnityEngine.Sprite::CreateSprite，
而引擎实际注册的是 UnityEngine.Sprite::CreateSprite_Injected。
所以除了 234 个缺失名，再补一批常见的变体名一起探测。
"""
import os
import re

SRC = r"D:\RiderProjects\ml-installer-06\icall_missing_real.txt"
OUT = r"D:\RiderProjects\ml-installer-06\mods-06\CSTI-MiniLoader-06\MissingIcalls.cs"

names = []
with open(SRC, encoding="utf-8", errors="replace") as f:
    for line in f:
        s = line.strip()
        if s.startswith("UnityEngine.") or s.startswith("Unity."):
            names.append(s)

variants = []
for n in names:
    if "::" not in n:
        continue
    t, m = n.split("::", 1)
    if t.endswith(("Sprite", "Texture2D", "Texture", "AudioClip", "ImageConversion", "Cubemap",
                   "RenderTexture", "Mesh", "Font", "TextAsset", "Resources", "Shader")):
        variants += [f"{t}::{m}_Injected", f"{t}::{m}Impl", f"{t}::{m}ImplArray",
                     f"{t}::Internal_{m}", f"{t}::get_{m}", f"{t}::{m}Internal"]
extra = [
    "UnityEngine.Sprite::CreateSpriteWithoutTextureScripting_Injected",
    "UnityEngine.Sprite::CreateWithoutTextureScripting_Injected",
    "UnityEngine.Sprite::CreateSprite_Injected",
    "UnityEngine.Sprite::Create_Injected",
    "UnityEngine.Texture2D::LoadRawTextureDataImpl",
    "UnityEngine.Texture2D::LoadRawTextureDataImplArray",
    "UnityEngine.Texture2D::SetPixelsImpl",
    "UnityEngine.Texture2D::ApplyImpl",
    "UnityEngine.Texture2D::GetRawTextureData",
    "UnityEngine.Texture2D::get_isReadable",
    "UnityEngine.ImageConversion::LoadImage",
    "UnityEngine.ImageConversion::EncodeToPNG",
    "UnityEngine.Sprite::get_texture",
    "UnityEngine.Sprite::get_rect",
]

allnames = []
for n in names + extra + variants:
    if n not in allnames:
        allnames.append(n)

with open(OUT, "w", encoding="utf-8") as f:
    f.write("// 自动生成（tools/gen_icall_probe.py）：缺失 ICall 探测名单\n")
    f.write("// 目的：裁剪版引擎可能只是 ICall 名字对不上（_Injected / Impl 变体），挨个解析一遍看有没有命中的。\n")
    f.write("namespace CSTI_MiniLoader\n{\n    public static class MissingIcalls\n    {\n")
    f.write("        public static readonly string[] Names =\n        {\n")
    for n in allnames:
        f.write('            "%s",\n' % n)
    f.write("        };\n    }\n}\n")

print("生成", OUT, "共", len(allnames), "个名字（缺失 %d + 变体/额外 %d）" % (len(names), len(allnames) - len(names)))
