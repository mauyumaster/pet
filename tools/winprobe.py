#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从外部量「窗口状态」—— 不是量画面（画面见 window-render-forensics 里那套做差仪器）。

为什么要有它（2026-09-25 真故障：用户报「桌宠无法保持在所有界面上方」）：
    这条故障**读代码读不出来**。`Topmost = Cfg.Topmost` 那行看着完全正确，
    而现场是 —— `config.json` 里 `"topmost": true`，
    活着的那个窗口 `GWL_EXSTYLE = 0x08080080`（TOOLWINDOW|NOACTIVATE|LAYERED），
    **0x00000008（WS_EX_TOPMOST）不在里面**。也就是「她自认置顶，系统不认」。
    一次外部量测就定了案；此前靠读代码只能猜。

⚠ 为什么不用 PowerShell：本机 `Add-Type`（PowerShell 侧 P/Invoke）被安全策略拦，
  这条路走不通。`Python + ctypes` 是通的，而且**能查一个正在跑的别人的进程**，不用改它一行代码。

用法：
    python tools/winprobe.py                # 顶层窗口 z 序总览（标出谁在置顶带里）
    python tools/winprobe.py 阿助            # 只看标题/类名含该子串的窗口，并列出它的同进程兄弟
    python tools/winprobe.py --pid 149676   # 按进程号
    python tools/winprobe.py --json out.json

读出来的是什么：
    z     —— `EnumWindows` 的顺序 = 真实 z 序，**数字越小越靠上**。
    ex    —— `GWL_EXSTYLE`。要认的位（见 Native.cs 里的常量）：
            0x00000008 TOPMOST      ← 置顶带；没有它，任何普通窗口都能盖住你
            0x00000080 TOOLWINDOW   ← 不出现在任务栏/Alt-Tab
            0x00000020 TRANSPARENT  ← 鼠标穿透
            0x00080000 LAYERED      ← 分层窗（WPF AllowsTransparency / 每像素透明）
            0x08000000 NOACTIVATE   ← 点击不激活
    ⚠ **判断「某某功能到底生效没有」看这些位，不要看程序自己的属性值** ——
      属性可以自认达标而底层状态早坏了（依赖属性值没变就根本不触发回调）。
"""
import ctypes
import json
import sys
from ctypes import wintypes

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

GWL_EXSTYLE = -20
GWLP_HWNDPARENT = -8
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

FLAGS = [
    (0x00000008, "TOPMOST"),
    (0x00000080, "TOOLWINDOW"),
    (0x00000020, "TRANSPARENT"),
    (0x00080000, "LAYERED"),
    (0x08000000, "NOACTIVATE"),
    (0x00040000, "APPWINDOW"),
]

u32.GetWindowLongW.restype = ctypes.c_long
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.IsWindowVisible.argtypes = [wintypes.HWND]

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def text(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(h, b, 512)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(512)
    u32.GetClassNameW(h, b, 512)
    return b.value


def pid_of(h):
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def ex_of(h):
    return u32.GetWindowLongW(h, GWL_EXSTYLE) & 0xFFFFFFFF


def flags_of(ex):
    return [n for bit, n in FLAGS if ex & bit]


def image_path(pid):
    h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(1024)
        n = wintypes.DWORD(1024)
        if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
            return buf.value
    finally:
        k32.CloseHandle(h)
    return ""


def all_top_level():
    """按 z 序返回全部顶层窗口（最上在前）。"""
    out = []
    u32.EnumWindows(WNDENUMPROC(lambda h, l: (out.append(h), True)[1]), 0)
    return out


def main(argv):
    want_json = None
    if "--json" in argv:
        i = argv.index("--json")
        want_json = argv[i + 1]
        del argv[i:i + 2]

    pid_filter = None
    if "--pid" in argv:
        i = argv.index("--pid")
        pid_filter = int(argv[i + 1])
        del argv[i:i + 2]

    needle = argv[0] if argv else None

    wins = all_top_level()
    rows = []
    for z, h in enumerate(wins):
        ex = ex_of(h)
        rows.append({
            "z": z, "hwnd": h, "pid": pid_of(h), "visible": bool(u32.IsWindowVisible(h)),
            "class": cls(h), "title": text(h), "ex": ex, "flags": flags_of(ex),
            "topmost": bool(ex & 0x00000008),
        })

    if pid_filter is not None:
        hits = [r for r in rows if r["pid"] == pid_filter]
    elif needle:
        hits = [r for r in rows
                if needle in r["title"] or needle in r["class"]
                or needle in image_path(r["pid"])]
    else:
        hits = None

    if hits is None:
        print("z序 | 置顶位 | pid | 可见 | class | title")
        print("---")
        for r in rows:
            if not r["visible"]:
                continue
            print("%4d | %-6s | %7d | %s | %-32s | %s"
                  % (r["z"], "TOPMOST" if r["topmost"] else "", r["pid"],
                     "是" if r["visible"] else "否", r["class"][:32], r["title"][:40]))
        print()
        n_top = sum(1 for r in rows if r["topmost"] and r["visible"])
        print("可见的置顶窗口 %d 个（上面那些 z 更小的就是压得住别人的）" % n_top)
        return 0

    if not hits:
        print("没找到匹配的窗口。")
        return 1

    pids = sorted(set(r["pid"] for r in hits))
    print("匹配到 %d 个窗口，来自 %d 个进程。" % (len(hits), len(pids)))
    for p in pids:
        print("  pid %-8d %s" % (p, image_path(p) or "(拿不到镜像路径)"))
    print()

    for r in hits:
        print("hwnd=%-12d z=%-5d 可见=%-3s pid=%-8d ex=0x%08X [%s]"
              % (r["hwnd"], r["z"], "是" if r["visible"] else "否", r["pid"], r["ex"],
                 ",".join(r["flags"])))
        print("    class = %s" % r["class"])
        print("    title = %s" % r["title"])

    print()
    for r in hits:
        if r["visible"]:
            print("⚠ 可见窗口 hwnd=%d 的置顶位 = %s"
                  % (r["hwnd"], "有" if r["topmost"] else "**无**（任何普通窗口都能盖住它）"))

    if want_json:
        with open(want_json, "w", encoding="utf-8") as f:
            json.dump({"all": rows, "hits": hits}, f, ensure_ascii=False, indent=2)
        print("已写 %s" % want_json)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
