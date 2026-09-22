#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""以真实的 .lnk 为模板重建快捷方式 —— 复用 Windows 写的 Header / IDList / LinkInfo，
只重写 StringData（名称 / 工作目录 / 图标）。

为什么不再从零构造
------------------
tools/make_shortcut.py（旧版）从零拼 .lnk，2026-09-21 用它建的「阿助.lnk」在
Windows 里表现为「空白、无目标、双击没反应」——回读字节发现 **HeaderCLSID 最后一
个字节写成了 00，应为 46**（正确值 00021401-0000-0000-C000-000000000046）。
这个 CLSID 是 Shell Link 的文件签名：错一字节，Explorer 就认为它不是快捷方式。

除此之外还差两处，单靠改 CLSID 不足以让人放心：
  1. 没有 LinkTargetIDList（Shell 主要靠 IDList 定位，LinkInfo 只是兜底）；
  2. LinkInfo 的 VolumeID 填了 Serial=0 / 卷标空，而真实值是
     Serial=0xDC6828C7 / "windows"。

IDList 是 shell 命名空间的 PIDL 序列（每项含长度、类型、Unicode 名、排序键、
时间戳…），手写几乎必错。而 IDList 的正确性由 Windows 保证 —— 所以本工具改成
**拿一份 Windows 生成的、结构正确的 lnk 当模板，复用它的 Header/IDList/LinkInfo**，
只把 StringData 换成我们要的字，再原样保留 ExtraData。

用法
----
    # 看结构（诊断）
    python lnk_rebuild.py --inspect 某.lnk

    # 以模板重建（复用 IDList / LinkInfo，只换字符串）
    python lnk_rebuild.py --tpl 模板.lnk --out 新.lnk \
        --name "启动桌面宠物阿助" --icon "D:\\...\\pet.ico"

生成后会立刻自校验（CLSID / 结构块 / VolumeID 与真实卷比对 / 目标与图标是否存在）。
"""

import argparse
import ctypes
import os
import struct
import sys

GOOD_CLSID = bytes.fromhex("0114020000000000c000000000000046")
STRING_ORDER = [
    (0x4, "NAME"),
    (0x8, "RELATIVE_PATH"),
    (0x10, "WORKING_DIR"),
    (0x20, "ARGS"),
    (0x40, "ICON_LOCATION"),
]


# ------------------------------------------------------------------ 解析
def parse(d):
    if len(d) < 76:
        raise ValueError("文件太小，不像 Shell Link")
    r = {}
    r["size"] = len(d)
    r["hdr_size"] = struct.unpack_from("<I", d, 0)[0]
    r["clsid"] = d[4:20]
    r["flags"] = struct.unpack_from("<I", d, 20)[0]
    r["attrs"] = struct.unpack_from("<I", d, 24)[0]
    r["showcmd"] = struct.unpack_from("<I", d, 60)[0]
    off = r["hdr_size"]

    r["idlist"] = None
    r["linkinfo"] = None
    if r["flags"] & 0x1:                      # HasLinkTargetIDList
        n = struct.unpack_from("<H", d, off)[0]
        r["idlist_size"] = n
        r["idlist"] = d[off:off + 2 + n]
        off += 2 + n
    if r["flags"] & 0x2:                      # HasLinkInfo
        n = struct.unpack_from("<I", d, off)[0]
        r["linkinfo_size"] = n
        r["linkinfo"] = d[off:off + n]
        off += n

    uni = bool(r["flags"] & 0x80)
    fields = {}
    cur = off
    for bit, label in STRING_ORDER:
        if r["flags"] & bit:
            cnt = struct.unpack_from("<H", d, cur)[0]
            nb = cnt * 2 if uni else cnt
            raw = d[cur + 2:cur + 2 + nb]
            fields[label] = {
                "off": cur,
                "count": cnt,
                "raw": raw,
                "text": raw.decode("utf-16-le" if uni else "gbk", errors="replace"),
            }
            cur += 2 + nb
    r["fields"] = fields
    r["sd_end"] = cur
    r["extra"] = d[cur:]
    r["unicode"] = uni

    # LinkInfo 里的 VolumeID + LocalBasePath
    r["vol"] = None
    r["lbp"] = None
    if r["linkinfo"]:
        li = r["linkinfo"]
        vol_off, lbp_off = struct.unpack_from("<II", li, 12)
        if vol_off:
            v = li[vol_off:]
            v_size, drvtype, serial = struct.unpack_from("<III", v, 0)
            lbl_off = struct.unpack_from("<I", v, 12)[0]
            label = b""
            if lbl_off:
                end = v.index(b"\x00", lbl_off)
                label = v[lbl_off:end]
            r["vol"] = {"size": v_size, "drivetype": drvtype,
                        "serial": serial, "label": label}
        if lbp_off:
            end = li.index(b"\x00", lbp_off)
            r["lbp"] = li[lbp_off:end].decode("gbk", errors="replace")
    return r


def volume_of(drive):
    """读真实的卷标与卷序列号（LinkInfo 的 VolumeID 应当与之一致）。"""
    buf = ctypes.create_unicode_buffer(261)
    serial = ctypes.c_ulong(0)
    ok = ctypes.windll.kernel32.GetVolumeInformationW(
        drive, buf, 261, ctypes.byref(serial), None, None, None, 0)
    return (buf.value, serial.value) if ok else (None, None)


# ------------------------------------------------------------------ 重建
def rebuild(tpl_bytes, name=None, workdir=None, icon=None):
    r = parse(tpl_bytes)
    if not (r["flags"] & 0x1):
        raise SystemExit("[!] 模板缺少 LinkTargetIDList —— 它不是好模板，换一个。")
    if not (r["flags"] & 0x2):
        raise SystemExit("[!] 模板缺少 LinkInfo，换一个。")

    # 用模板自身的字节反推「CharacterCount 是否含结尾 NUL」，不靠猜。
    uses_nul = any(
        f["raw"].endswith(b"\x00\x00" if r["unicode"] else b"\x00")
        for f in r["fields"].values()
    )

    def encode(s):
        raw = s.encode("utf-16-le" if r["unicode"] else "gbk")
        if uses_nul:
            raw += b"\x00\x00" if r["unicode"] else b"\x00"
        unit = 2 if r["unicode"] else 1
        return struct.pack("<H", len(raw) // unit) + raw

    out_sd = b""
    for bit, label in STRING_ORDER:
        if not (r["flags"] & bit):
            continue
        if label == "NAME" and name is not None:
            out_sd += encode(name)
        elif label == "WORKING_DIR" and workdir is not None:
            out_sd += encode(workdir)
        elif label == "ICON_LOCATION" and icon is not None:
            out_sd += encode(icon)
        else:
            f = r["fields"][label]
            out_sd += struct.pack("<H", f["count"]) + f["raw"]

    head = tpl_bytes[:r["hdr_size"]]
    return head + r["idlist"] + r["linkinfo"] + out_sd + r["extra"], uses_nul


# ------------------------------------------------------------------ 校验
def verify(path):
    d = open(path, "rb").read()
    r = parse(d)
    checks = []

    def ck(label, ok, detail=""):
        checks.append((label, bool(ok), detail))

    ck("CLSID == 00021401-0000-0000-C000-000000000046",
       r["clsid"] == GOOD_CLSID, r["clsid"].hex())
    ck("HeaderSize == 76", r["hdr_size"] == 76, str(r["hdr_size"]))
    ck("有 LinkTargetIDList（Shell 定位的主力）", r["flags"] & 0x1,
       "flags=0x%X" % r["flags"])
    ck("有 LinkInfo", r["flags"] & 0x2, "")
    ck("有 Name", r["flags"] & 0x4, "")
    ck("有 WorkingDir", r["flags"] & 0x10, "")
    ck("有 IconLocation", r["flags"] & 0x40, "")
    ck("IsUnicode", r["flags"] & 0x80, "")

    # VolumeID 必须与真实卷一致，否则 Shell 可能认为目标在别的卷上
    if r["vol"] and r["lbp"]:
        drive = os.path.splitdrive(r["lbp"])[0] + "\\"
        label, serial = volume_of(drive)
        ck("VolumeID 的序列号 == 真实卷 %s" % drive,
           r["vol"]["serial"] == serial,
           "lnk=0x%08X 实际=0x%08X" % (r["vol"]["serial"], serial or 0))
        ck("VolumeID 的卷标 == 真实卷",
           r["vol"]["label"].decode("gbk", errors="replace") == label,
           "%r vs %r" % (r["vol"]["label"], label))

    # IDList 最后一项的名字 与 LocalBasePath 的文件名 应当一致
    if r["idlist"] and r["lbp"]:
        # IDList 尾部带目标名（UTF-16），粗暴但有效的判据
        want = os.path.basename(r["lbp"])
        ck("IDList / LocalBasePath 指向同一个文件",
           want.encode("utf-16-le") in r["idlist"], want)

    lbp = r["lbp"]
    ck("LocalBasePath 指向的文件存在", lbp and os.path.isfile(lbp), lbp or "")

    ico = r["fields"].get("ICON_LOCATION", {}).get("text", "").split(",")[0]
    if ico:
        ck("IconLocation 指向的文件存在", os.path.isfile(ico), ico)

    ck("字节消耗自洽（StringData 结束后仍有 ExtraData 或被正常截断）",
       r["sd_end"] <= r["size"], "%d <= %d" % (r["sd_end"], r["size"]))
    return checks, r


SHOWCMD_NAMES = {0: "SW_HIDE 窗口完全隐藏",
                 1: "SW_SHOWNORMAL",
                 3: "SW_SHOWMAXIMIZED",
                 7: "SW_SHOWMINNOACTIVE 最小化且不抢焦点"}


def show(r):
    print("  size=%d  HeaderSize=%d  flags=0x%X" % (r["size"], r["hdr_size"], r["flags"]))
    print("  CLSID = %s  %s" % (r["clsid"].hex(),
                                 "OK" if r["clsid"] == GOOD_CLSID else "*** BAD ***"))
    # ShowCommand 在 Header 偏移 60。⚠ 这个字段曾经被写/读双方一起错位到 64
    # （见 tools/make_shortcut.py 里的说明），所以这里单列出来打印，便于核对。
    print("  ShowCommand(@60) = %d  %s" %
          (r["showcmd"], SHOWCMD_NAMES.get(r["showcmd"], "?")))
    print("  IDList   = %s" % (("IDListSize=%d" % r["idlist_size"]) if r["idlist"] else "** 无 **"))
    print("  LinkInfo = %s" % (("size=%d" % r["linkinfo_size"]) if r["linkinfo"] else "无"))
    if r["vol"]:
        print("     VolumeID serial=0x%08X label=%r" %
              (r["vol"]["serial"], r["vol"]["label"].decode("gbk", errors="replace")))
    if r["lbp"]:
        print("     LocalBasePath = %s" % r["lbp"])
    for bit, label in STRING_ORDER:
        f = r["fields"].get(label)
        if f:
            print("     %-14s count=%-3d off=%-5d %r" % (label, f["count"], f["off"], f["text"]))
    print("  StringData 结束于 %d，ExtraData %d B" % (r["sd_end"], len(r["extra"])))


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--inspect", metavar="LNK", help="打印结构后退出")
    ap.add_argument("--tpl", metavar="LNK", help="模板 lnk（必须是 Windows 生成的好文件）")
    ap.add_argument("--out", metavar="LNK", help="输出路径")
    ap.add_argument("--name", help="改 名称")
    ap.add_argument("--workdir", help="改 工作目录")
    ap.add_argument("--icon", help="改 图标（path 或 path,index）")
    args = ap.parse_args()

    if args.inspect:
        d = open(args.inspect, "rb").read()
        print("== %s ==" % args.inspect)
        r = parse(d)
        show(r)
        print("-- 校验 --")
        checks, _ = verify(args.inspect)
        for label, ok, detail in checks:
            print("   [%s] %s%s" % ("OK " if ok else "BAD", label,
                                    ("  <- " + detail) if detail else ""))
        return 0 if all(c[1] for c in checks) else 1

    if not (args.tpl and args.out):
        ap.error("要么给 --inspect，要么给 --tpl 和 --out")

    tpl = open(args.tpl, "rb").read()
    out, uses_nul = rebuild(tpl, name=args.name, workdir=args.workdir, icon=args.icon)
    open(args.out, "wb").write(out)
    print("已写出 %s（%d B；模板计数语义: %s）" %
          (args.out, len(out), "含结尾 NUL" if uses_nul else "不含 NUL"))

    checks, _ = verify(args.out)
    print("-- 校验 --")
    bad = 0
    for label, ok, detail in checks:
        if not ok:
            bad += 1
        print("   [%s] %s%s" % ("OK " if ok else "BAD", label,
                                ("  <- " + detail) if detail else ""))
    print("=> %d 项检查，%d 项不合格" % (len(checks), bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
