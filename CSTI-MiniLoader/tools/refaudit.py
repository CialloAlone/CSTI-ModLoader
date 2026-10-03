"""引用定位方式审计（离线，JSON 侧证据）。

扫描作者目录里**全部** JSON，对每一个 `*WarpData` 键统计：
  · 值的形态：GUID（32 位十六进制）/ 名字 / 混合 / 数字
  · 该键对应的 *WarpType
输出按 **字段名** 聚合（不抽样、不截断），用于回答"JSON 里给的是 GUID 还是名字"。
"""
import json
import os
import re
import sys
from collections import defaultdict

ROOT = sys.argv[1] if len(sys.argv) > 1 else r"D:\RiderProjects\csti\BepInEx\plugins\Windy"
GUID = re.compile(r"^[0-9a-fA-F]{32}$")


def kind(v):
    if isinstance(v, str):
        return "guid" if GUID.match(v) else "name"
    if isinstance(v, (int, float)):
        return "number"
    return "other"


def walk(o, path, out, ftype):
    if isinstance(o, dict):
        for k, v in o.items():
            if k.endswith("WarpData"):
                fld = k[:-8]
                wt = o.get(fld + "WarpType")
                vals = v if isinstance(v, list) else [v]
                ks = sorted({kind(x) for x in vals})
                out[fld][tuple(ks)].append((wt, len(vals), path))
            walk(v, path, out, ftype)
    elif isinstance(o, list):
        for x in o:
            walk(x, path, out, ftype)


def main():
    agg = defaultdict(lambda: defaultdict(list))
    files = 0
    for dirpath, _dirs, names in os.walk(ROOT):
        for n in names:
            if not n.endswith(".json"):
                continue
            fp = os.path.join(dirpath, n)
            try:
                d = json.load(open(fp, encoding="utf-8-sig"))
            except Exception:
                continue
            files += 1
            agg_local = defaultdict(lambda: defaultdict(list))
            walk(d, os.path.relpath(fp, ROOT), agg_local, None)
            for fld, forms in agg_local.items():
                for form, items in forms.items():
                    agg[fld][form].extend(items)

    print("扫描 JSON 文件数 = %d\n" % files)
    print("%-30s %-14s %-8s %-7s %s" % ("字段", "JSON 形态", "WarpType", "条数", "样例出处"))
    print("-" * 110)
    for fld in sorted(agg):
        for form, items in sorted(agg[fld].items()):
            wts = sorted({str(i[0]) for i in items})
            total = sum(i[1] for i in items)
            sample = items[0][2]
            print("%-30s %-14s %-8s %-7d %s" % (fld, "+".join(form), ",".join(wts), total, sample))


if __name__ == "__main__":
    main()
