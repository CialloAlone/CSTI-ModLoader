"""打印指定 GSM 条目的"新增动作"完整 JSON（离线，用于看"能不能拖上去"的判定字段）。

用法: python gsmdump.py <组名> <GUID前8位> [元素下标]
不传参数时，打印每组第一条的键结构概览。
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import archdump as A

PATH = os.environ.get("WINDY_ARCH", A.PATH)


def load():
    data = open(PATH, "rb").read()
    r = A.R(data)
    r.s()
    js = None
    while True:
        name = r.s()
        if name == A.END:
            break
        if name == "JsonsBLK":
            js = A.decode_jsons(r)
        else:
            A.handler(name, r)
    return [(ls, o) for ls, o in js if ls and ls[0] == "GameSourceModify"]


def main():
    gsm = load()
    if len(sys.argv) < 3:
        # 概览：每组的第一个条目，打印其元素对象的键名
        seen = set()
        for ls, o in gsm:
            g = ls[1]
            if g in seen:
                continue
            seen.add(g)
            for k, v in o.items():
                if not k.endswith("WarpType"):
                    continue
                fld = k[:-8]
                d = o.get(fld + "WarpData")
                print("== %-22s %s 字段=%s WarpType=%s ==" % (g, ls[-1], fld, v))
                if isinstance(d, list) and d and isinstance(d[0], dict):
                    print("   元素键: %s" % ", ".join(d[0].keys()))
                    print("   " + json.dumps(d[0], ensure_ascii=False)[:600])
                print()
        return

    g, guid8 = sys.argv[1], sys.argv[2]
    idx = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    for ls, o in gsm:
        if ls[1] != g or guid8 not in ls[-1]:
            continue
        print("路径: %s" % "/".join(ls))
        for k, v in o.items():
            if not k.endswith("WarpType"):
                continue
            fld = k[:-8]
            d = o.get(fld + "WarpData")
            print("字段=%s WarpType=%s 元素数=%s" % (fld, v, len(d) if isinstance(d, list) else 1))
            if isinstance(d, list) and idx < len(d):
                print(json.dumps(d[idx], ensure_ascii=False, indent=2))
        return
    print("未找到该条目")


if __name__ == "__main__":
    main()
