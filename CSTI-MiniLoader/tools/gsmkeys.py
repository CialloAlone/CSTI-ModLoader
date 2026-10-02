"""列出 .modArch_V3 里全部 GameSourceModify 条目的 JSON 键名（离线，不打设备）。

用途：核对 GSM 的 `*WarpData` 键名与目标对象的字段名是否一致（例如到底是
`DismantleActionsWarpData` 还是 `CardInteractionsWarpData`），以及 63 条各自改哪些字段。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import archdump as A

PATH = sys.argv[1] if len(sys.argv) > 1 else A.PATH


def main():
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
    if js is None:
        print("没有 JsonsBLK")
        return

    gsm = [(ls, o) for ls, o in js if ls and ls[0] == "GameSourceModify"]
    print("GameSourceModify 条目 = %d\n" % len(gsm))

    # 键名分布
    import collections
    keycount = collections.Counter()
    wt = collections.Counter()
    for ls, o in gsm:
        for k in o.keys():
            keycount[k] += 1
        for k, v in o.items():
            if k.endswith("WarpType"):
                wt[v] += 1

    print("== 全部键名（出现次数）==")
    for k, v in keycount.most_common():
        print("   %-42s %d" % (k, v))
    print("\n== WarpType 取值分布 ==")
    for k, v in sorted(wt.items()):
        print("   WarpType=%s : %d 条" % (k, v))

    print("\n== 逐条：路径 / GUID / 键名(=字段) / WarpType / 元素数 ==")
    for i, (ls, o) in enumerate(gsm, 1):
        path = "/".join(ls)
        guid = path.split("/")[-1].replace(".json", "")
        for k, v in o.items():
            if not k.endswith("WarpType"):
                continue
            fld = k[:-8]
            d = o.get(fld + "WarpData")
            n = len(d) if isinstance(d, list) else 1
            etype = "?"
            if isinstance(d, list) and d and isinstance(d[0], dict):
                etype = "obj"
            elif isinstance(d, list) and d:
                etype = "str"
            print("[%2d/%d] %-34s %s 字段=%-22s WarpType=%-2s 元素=%d(%s)"
                  % (i, len(gsm), path.split("/")[1], guid, fld, v, n, etype))


if __name__ == "__main__":
    main()
