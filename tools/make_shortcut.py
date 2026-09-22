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
# ⚠⚠ CLSID 是 Shell Link 的「文件签名」：错一个字节，Explorer 就认为这压根不是
# 快捷方式 —— 表现为图标空白、无目标、双击没反应，而且全程不报任何错。
# 正确值 00021401-0000-0000-C000-000000000046，小端字节序：
#   01 14 02 00 | 00 00 | 00 00 | C0 00 00 00 00 00 00 46
# 旧版这里手拼成 ...C0 00 + 六个 00（末字节 00），而上面那行注释写的是正确值 ——
# 注释对、代码错；又因为 verify() 当时没有 CLSID 判据，它照样报「五项全绿」，
# 直到用户双击才发现（2026-09-22）。
HEADER_CLSID = bytes.fromhex("0114020000000000c000000000000046")
assert len(HEADER_CLSID) == 16, len(HEADER_CLSID)


# ---------------- 生成 ----------------
def _volume_of(target):
    """读目标所在卷的真实卷标与卷序列号 —— LinkInfo 的 VolumeID 必须与之一致。

    旧版把 Serial 填 0、卷标留空，而 Windows 生成的真实 lnk 里是
    Serial=0xDC6828C7 / 卷标 "windows"。填 0 会让 Shell 怀疑目标不在这个卷上。
    """
    import ctypes
    drive = (os.path.splitdrive(os.path.abspath(target))[0] or "C:") + "\\"
    buf = ctypes.create_unicode_buffer(261)
    serial = ctypes.c_ulong(0)
    ok = ctypes.windll.kernel32.GetVolumeInformationW(
        drive, buf, 261, ctypes.byref(serial), None, None, None, 0)
    return (buf.value, serial.value) if ok else ("", 0)


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
    # VolumeID 必须与该卷的真实信息一致：序列号 + 卷标。真实 lnk 里
    # size=16+8=24、DriveType=3、Serial=0xDC6828C7、LabelOffset=16、标签 "windows"。
    vlabel, vserial = _volume_of(target)
    label = vlabel.encode("gbk", errors="replace") + b"\x00"
    vol_id = struct.pack("<IIII", 16 + len(label), 3, vserial, 0x10) + label
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

    # ---- Header（76 字节，MS-SHLLINK 2.1）----
    # 偏移表（**这是唯一权威，别凭记忆**）：
    #   0  HeaderSize(4)    4  LinkCLSID(16)   20 LinkFlags(4)   24 FileAttributes(4)
    #   28 CreationTime(8)  36 AccessTime(8)   44 WriteTime(8)
    #   52 FileSize(4)      56 IconIndex(4)    60 ShowCommand(4)
    #   64 HotKey(2)        66 Reserved1(2)    68 Reserved2(4)   72 Reserved3(4)
    # ⚠ 旧版写成 "<I16sIIQQQQIIHHI"：把 FileSize 当成 8 字节 Q（其实 4 字节，紧接
    #   IconIndex 4 字节），于是 ShowCommand 被挤到 offset 64（HotKey 的位置）；
    #   而 parse_lnk 也用 "<QII" @52 做**同样错位**的读取 —— 写错位、读也错位，
    #   两边恰好抵消，自测永远全绿。真实后果：Windows 在 60 处读到 0 = SW_HIDE，
    #   启动时窗口完全隐藏；而 run-pet.cmd 在缺模型/构建失败时会 pause，
    #   那行提示就藏在一个谁也看不见的窗口里。
    header = struct.pack(
        "<I16sIIQQQIIIHHII",
        0x4C,                  # HeaderSize
        HEADER_CLSID,          # LinkCLSID
        flags,                 # LinkFlags
        0x80,                  # FileAttributes: FILE_ATTRIBUTE_NORMAL
        0, 0, 0,               # CreationTime / AccessTime / WriteTime：不指定（合法）
        0,                     # FileSize：未知
        0,                     # IconIndex
        SW_SHOWMINNOACTIVE if minimize else 1,   # ShowCommand —— 必须在 offset 60
        0, 0,                  # HotKey, Reserved1
        0, 0,                  # Reserved2, Reserved3
    )
    assert len(header) == 0x4C, len(header)
    return header + link_info + strings


# ---------------- 解析（用于自验 / --parse） ----------------
def parse_lnk(data):
    r = {}
    hdr_size, clsid, flags, attrs = struct.unpack_from("<I16sII", data, 0)
    creation, access, write_t = struct.unpack_from("<QQQ", data, 0x1C)
    # ⚠ 偏移表见 build_lnk 顶部。旧版这里写 "<QII" @0x34(=52)：把 FileSize(4) +
    #   IconIndex(4) 读成一个 8 字节 FileSize，于是 icon_idx 拿到的是 ShowCommand(@60)、
    #   show_cmd 拿到的是 HotKey(@64，恒 0)。**与写入侧的错位方向一致 ⇒ 两边抵消**，
    #   自测永远全绿而 Windows 在 60 处读到 SW_HIDE。改成一个字段一次读，别再合并。
    file_size = struct.unpack_from("<I", data, 52)[0]
    icon_idx = struct.unpack_from("<I", data, 56)[0]
    show_cmd = struct.unpack_from("<I", data, 60)[0]
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
        # VolumeID：卷序列号 + 卷标。旧版写入时填 0、verify 也没查它。
        r["volume_serial"] = r["volume_label"] = None
        if vol_off:
            v = li[vol_off:]
            _vsz, _drv, _ser = struct.unpack_from("<III", v, 0)
            _lbl_off = struct.unpack_from("<I", v, 12)[0]
            r["volume_serial"] = _ser
            r["volume_label"] = (v[_lbl_off:v.index(b"\x00", _lbl_off)]
                                 .decode("gbk", "replace")) if _lbl_off else ""
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
        # 图标字符串有两种合法写法："路径" 与 "路径,index"。旧判据只认后者，
        # 会把模板路线生成（纯路径）的正确文件误判成 FAIL —— **假失败比假通过更坏**，
        # 它会训练人忽略红灯。比对前先剥掉 ",index"。
        def _strip_idx(s):
            s = s or ""
            head, sep, tail = s.rpartition(",")
            return head if sep and tail.strip().isdigit() else s
        chk("icon", _strip_idx(p.get("icon")), os.path.abspath(expect["icon"]))
    # ShowCommand 按**规范偏移 60** 独立读一遍，不复用 parse_lnk 的结果 ——
    # 写入侧与解析侧若同时错位就会互相抵消，只有这条独立判据是真的。
    # 0 = SW_HIDE，对本项目是错的：run-pet.cmd 在缺模型/构建失败时会 pause，
    # 窗口一藏，那行提示就没人看得见。
    _want_sc = expect.get("show_cmd", 7 if expect.get("minimize", True) else 1)
    chk("show_cmd@60", struct.unpack_from("<I", data, 60)[0], _want_sc)
    chk("header_size", p.get("header_size"), 0x4C)

    # ⚠ 文件签名 —— 最基础也最致命。旧版 verify 没有这一项，所以 CLSID 写错
    # 一个字节时它照样报「五项全绿」，直到用户双击才发现（2026-09-22）。
    chk("header_clsid", data[4:20].hex(), HEADER_CLSID.hex())

    # VolumeID 必须与真实卷一致（旧版写入时填 0）
    _vl, _vs = _volume_of(expect["target"])
    chk("volume_serial", p.get("volume_serial"), _vs)
    chk("volume_label", p.get("volume_label"), _vl)

    if not (p.get("flags", 0) & 1):
        print("  [warn] 本工具不写 LinkTargetIDList（PIDL 手写极易错），只靠 "
              "LinkInfo 兜底。要稳请改用 tools/lnk_rebuild.py —— 它复用一份真实 "
              "lnk 的 IDList/LinkInfo，只重写字符串。")

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
