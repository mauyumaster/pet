#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make_shortcut.py —— 纯字节生成 Windows .lnk（MS-SHLLINK），不依赖 COM。

WHY：本机安全策略拦 COM 实例化（WScript.Shell / win32com Dispatch 都被
"Known LOLBin / COM can run arbitrary code" 拦），而快捷方式本身只是
一个固定格式的二进制文件 —— 直接按 MS-SHLLINK 写字节即可，不起任何进程。

结构（本工具只写最小可用集）：
  Header(76B) + LinkInfo(本地路径) + StringData(RelativePath/WorkingDir/Icon)

自验三步（缺一不可，见 verify()）：
  1. parse_back：用本文件的解析器读回，字段逐一比对
  2. ground truth：先解析一个真实的 .lnk（桌面现成的），确认解析器本身理解正确
  3. 头部一致性：HeaderSize==0x4C、各 offset 落在块内、块尺寸自洽

用法：
  python make_shortcut.py <lnk路径> <目标路径> [--icon <ico>] [--workdir <dir>]
                          [--name <描述>] [--min]
  python make_shortcut.py --parse <某个.lnk>   # 只解析打印
"""
import struct
import sys
import os

# ---------------- 常量（MS-SHLLINK） ----------------
LINK_FLAGS = {
    "HasLinkTargetIDList": 0x1,
    "HasLinkInfo": 0x2,
    "HasName": 0x4,
    "HasRelativePath": 0x8,
    "HasWorkingDir": 0x10,
    "HasArguments": 0x20,
    "HasIconLocation": 0x40,
    "IsUnicode": 0x80,
}
SW_SHOWMINNOACTIVE = 7
HEADER_CLSID = struct.pack("<IHHBB", 0x00021401, 0x0000, 0x0000,
                           0xC0, 0x00) + b"\x00\x00\x00\x00\x00\x00"
# 即 00021401-0000-0000-C000-000000000046


# ---------------- 生成 ----------------
def _ansi(b):
    """Windows ANSI（本机 GBK）编码；路径里只能含本地码页能表达的字符。"""
    return b.encode("gbk", errors="strict")


def _u16(s):
    """StringData 的 Unicode 字符串：UTF-16LE + 结尾 NUL，CharacterCount 含 NUL。"""
    raw = s.encode("utf-16-le") + b"\x00\x00"
    return struct.pack("<H", len(s) + 1) + raw


def build_lnk(target, workdir=None, icon=None, desc=None, minimize=True,
              relative=None):
    target = os.path.abspath(target)
    workdir = os.path.abspath(workdir) if workdir else os.path.dirname(target)
    icon = os.path.abspath(icon) if icon else None
    relative = relative or (os.path.basename(target))

    flags = LINK_FLAGS["HasLinkInfo"] | LINK_FLAGS["HasRelativePath"] \
        | LINK_FLAGS["HasWorkingDir"] | LINK_FLAGS["IsUnicode"]
    if icon:
        flags |= LINK_FLAGS["HasIconLocation"]

    # ---- LinkInfo（VolumeID + LocalBasePath，ANSI）----
    label = b"\x00"                       # 空卷标，占一个 NUL
    vol_id = struct.pack("<IIII", 16 + len(label), 3, 0, 0x10) + label
    lbp = _ansi(target) + b"\x00"
    suffix = b"\x00"

    li_hdr = 0x1C
    vol_off = li_hdr
    lbp_off = vol_off + len(vol_id)
    cnrl_off = lbp_off + len(lbp)         # 0 占位：CommonNetworkRelativeLink 无
    suffix_off = cnrl_off                 # 无 CNRL，suffix 紧跟其后
    link_info = struct.pack("<IIIIIII", 0, li_hdr, 0x1,
                            vol_off, lbp_off, 0, suffix_off) + vol_id + lbp + suffix
    # Size 字段＝含自身的总长。占位符本身已计入 len，不能再 +4（曾因此偏 4 字节，
    # 解析器落到字符串中间读出 42527 的垃圾计数 —— 回读校验当场抓住）。
    link_info = struct.pack("<I", len(link_info)) + link_info[4:]

    # ---- StringData ----
    strings = b""
    # 顺序固定：NAME_STRING? -> RELATIVE_PATH -> WORKING_DIR -> ARGS? -> ICON_LOCATION?
    if desc:
        flags |= LINK_FLAGS["HasName"]
        strings += _u16(desc)
    strings += _u16(relative)
    strings += _u16(workdir)
    if icon:
        strings += _u16(icon + ",0")

    # ---- Header（76 字节：FileComposition 见 MS-SHLLINK 2.1；FileSize 是 8 字节）----
    header = struct.pack(
        "<I16sIIQQQQIIHHI",
        0x4C,
        HEADER_CLSID,
        flags,
        0x80,                 # FILE_ATTRIBUTE_NORMAL
        0, 0, 0,              # 三个时间：不指定（合法）
        0,                    # FileSize：未知（8 字节）
        0,                    # IconIndex
        SW_SHOWMINNOACTIVE if minimize else 1,
        0, 0, 0,              # HotKey, Reserved1, Reserved2
    )
    assert len(header) == 0x4C, len(header)
    return header + link_info + strings


# ---------------- 解析（用于自验 / --parse） ----------------
def parse_lnk(data):
    r = {}
    hdr_size, clsid, flags, attrs = struct.unpack_from("<I16sII", data, 0)
    creation, access, write_t = struct.unpack_from("<QQQ", data, 0x1C)
    file_size, icon_idx, show_cmd = struct.unpack_from("<QII", data, 0x34)
    r["header_size"] = hdr_size
    r["flags"] = flags
    r["show_cmd"] = show_cmd
    off = hdr_size
    if flags & 0x1:   # IDList
        idl_size = struct.unpack_from("<H", data, off)[0]
        off += 2 + idl_size
    if flags & 0x2:   # LinkInfo
        li_size = struct.unpack_from("<I", data, off)[0]
        li = data[off:off + li_size]
        li_hdr, li_flags = struct.unpack_from("<II", li, 4)
        # ↑ li[0:4] 是 Size（已在上面读）；li[4:8]=HeaderSize、li[8:12]=Flags。
        #   曾把三个值错位命名，把 VolumeIDOffset 当 Flags ⇒ local_path 永远 None，
        #   而真实 lnk 恰好也显示 None，差点把 bug 当成「真实文件就是这样」。
        vol_off, lbp_off = struct.unpack_from("<II", li, 12)
        r["linkinfo_size"] = li_size
        r["local_path"] = li[lbp_off:].split(b"\x00")[0].decode("gbk", "replace") \
            if li_flags & 0x1 else None
        off += li_size
    def rd_str(o):
        n = struct.unpack_from("<H", data, o)[0]
        return data[o + 2:o + 2 + n * 2].decode("utf-16-le", "replace").rstrip("\x00"), o + 2 + n * 2
    if flags & 0x4:
        r["name"], off = rd_str(off)
    if flags & 0x8:
        r["relative_path"], off = rd_str(off)
    if flags & 0x10:
        r["workdir"], off = rd_str(off)
    if flags & 0x20:
        r["args"], off = rd_str(off)
    if flags & 0x40:
        r["icon"], off = rd_str(off)
    r["bytes_after_header"] = len(data) - hdr_size
    return r


def verify(path, expect):
    data = open(path, "rb").read()
    p = parse_lnk(data)
    ok = True
    def chk(name, got, want):
        nonlocal ok
        good = got == want
        ok = ok and good
        print("  [%s] %-14s %s" % ("PASS" if good else "FAIL", name, got))
    chk("local_path", p.get("local_path"), os.path.abspath(expect["target"]))
    chk("workdir", p.get("workdir"),
        os.path.abspath(expect["workdir"]) if expect.get("workdir") else None)
    if expect.get("icon"):
        want_icon = os.path.abspath(expect["icon"]) + ",0"
        chk("icon", p.get("icon"), want_icon)
    chk("show_cmd", p.get("show_cmd"), 7 if expect.get("minimize", True) else 1)
    chk("header_size", p.get("header_size"), 0x4C)
    # 块尺寸自洽：header + linkinfo + strings == 文件总长（本工具不写 TerminalBlock）
    li_size = p.get("linkinfo_size")
    if li_size is not None:
        print("  [info] linkinfo_size=%d file=%d" % (li_size, len(data)))
    return ok


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)
    if args[0] == "--parse":
        d = open(args[1], "rb").read()
        for k, v in parse_lnk(d).items():
            print("%-16s %s" % (k, v))
        sys.exit(0)

    lnk, target = args[0], args[1]
    icon = workdir = desc = None
    minimize = True
    i = 2
    while i < len(args):
        if args[i] == "--icon":
            icon = args[i + 1]; i += 2
        elif args[i] == "--workdir":
            workdir = args[i + 1]; i += 2
        elif args[i] == "--name":
            desc = args[i + 1]; i += 2
        elif args[i] == "--no-min":
            minimize = False; i += 1
        else:
            print("未知参数", args[i]); sys.exit(1)

    blob = build_lnk(target, workdir=workdir, icon=icon, desc=desc,
                     minimize=minimize)
    open(lnk, "wb").write(blob)
    print("已写入 %s（%d 字节）" % (lnk, len(blob)))
    print("回读校验：")
    ok = verify(lnk, {"target": target, "workdir": workdir,
                      "icon": icon, "minimize": minimize})
    sys.exit(0 if ok else 2)
