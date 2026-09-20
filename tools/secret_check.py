# -*- coding: utf-8 -*-
"""
凭据文件结构体检（不打印任何密文）。

StatusProbe 读这两种文件的约定：首个非 `---` 行 = URL；其余含 `:` 的行 = header。
本脚本只回答「文件在不在、长得对不对、有哪些 header」，**绝不输出 cookie / token 的值**。
用法：python secret_check.py
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PET = os.path.dirname(HERE)          # .../pet


def redact(v):
    v = (v or "").strip()
    if not v:
        return "(空)"
    if len(v) <= 4:
        return "*" * len(v) + " (len=%d)" % len(v)
    return v[:2] + "…" + "*" * min(6, len(v) - 2) + " (len=%d)" % len(v)


def redact_url(u):
    u = (u or "").strip()
    if not u:
        return "(空)"
    # 只留 scheme://host/path，抹掉 query（可能带 token）
    q = u.find("?")
    tail = "  [含 query，已抹去]" if q >= 0 else ""
    return (u[:q] if q >= 0 else u) + tail


def check(name):
    p = os.path.join(PET, name)
    print("=" * 60)
    print("文件：%s" % name)
    if not os.path.exists(p):
        print("  ❌ 不存在（此路凭据不生效）")
        return
    raw = open(p, "rb").read()
    print("  存在：%d 字节" % len(raw))
    crlf = raw.count(b"\r\n")
    lf = raw.count(b"\n") - crlf
    print("  行尾：CRLF=%d  LF-only=%d%s" % (crlf, lf, "  ⚠ 混行尾" if crlf and lf else ""))
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError as e:
        print("  ❌ 不是 UTF-8：%s" % e)
        return
    url = None
    headers = []
    for raw_line in text.splitlines():
        t = raw_line.strip()
        if t.startswith("---"):
            continue
        if url is None and t:
            url = t
            continue
        c = raw_line.find(":")
        if c > 0:
            headers.append(raw_line[:c].strip())
    print("  URL ：%s" % redact_url(url))
    print("  headers（%d 个）：%s" % (len(headers), ", ".join(headers) if headers else "(无)"))
    want = {"cookie", "x-user-id", "content-type", "authorization", "user-agent"}
    have = {h.lower() for h in headers}
    missing = sorted(x for x in want if x not in have)
    if missing:
        print("  ⚠ 常见但缺失：%s（视接口而定，不一定算错）" % ", ".join(missing))


if __name__ == "__main__":
    print("凭据体检 · 只报结构不报密文")
    for n in ("balance_secret.txt", "workbuddy_secret.txt"):
        check(n)
    print("=" * 60)
