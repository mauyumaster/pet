#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SVG 守卫 —— README 配图能不能在 GitHub 上正确渲染的离线判据。

WHY 需要它：GitHub 渲染仓库内 SVG 时会**剥掉 <style> 块与 class 属性**
（防 XSS），只认内联的 fill / stroke / font-size。所以：
  * 用 class 画出来的图，本地看着好好的，推到 GitHub 上**整片变黑**。
  * 属性拼错（例如 `stroke="x"/ fill="y">`）会破坏 XML，图**直接不显示**。
两种失败都是「本地看不出来、线上才显形」，不能靠人眼检查，必须有判据。

用法：
    python check-readme-svgs.py          # 查同目录所有 *.svg
    python check-readme-svgs.py a.svg    # 只查指定文件
退出码 0 = 全绿；1 = 有问题。
"""
import sys
import os
import re
import glob
import xml.etree.ElementTree as ET

# GitHub 会保留的内联演示属性
SIZE_TAGS = ('text', 'tspan')
PAINT_TAGS = ('rect', 'circle', 'ellipse', 'path', 'polygon', 'line')
ALL_TAGS = SIZE_TAGS + PAINT_TAGS + ('g', 'svg')


def check(path):
    """返回 (错误列表, 提示列表)。"""
    errs, notes = [], []
    raw = open(path, encoding='utf-8').read()

    # ---- 1. XML 必须合法（拼错属性＝整张图不显示）----
    try:
        ET.fromstring(raw)
    except ET.ParseError as e:
        errs.append('XML 不合法（图会完全不显示）: %s' % e)
        return errs, notes   # 后面依赖解析，直接返回

    # ---- 2. 不许有 <style> 块 ----
    if '<style' in raw:
        errs.append('含 <style> 块 —— GitHub 会剥掉，样式全部失效')

    # ---- 3. 不许有 class= ----
    n_cls = len(re.findall(r'\bclass\s*=', raw))
    if n_cls:
        errs.append('含 %d 处 class= —— GitHub 会剥掉，元素会退化成黑色填充' % n_cls)

    # ---- 4. 每个可见元素必须自带颜色 ----
    for tag in PAINT_TAGS:
        for m in re.finditer(r'<%s\b[^>]*>' % tag, raw):
            el = m.group(0)
            has_paint = ('fill=' in el) or ('stroke=' in el)
            if not has_paint:
                errs.append('<%s> 既无 fill 也无 stroke（会变黑块）: %s'
                            % (tag, el[:70]))

    # ---- 5. 每个 text 必须有 fill 和 font-size ----
    for m in re.finditer(r'<text\b[^>]*>', raw):
        el = m.group(0)
        if 'fill=' not in el:
            errs.append('<text> 缺 fill（会默认黑色，深色主题下看不见）: %s' % el[:70])
        if 'font-size=' not in el:
            errs.append('<text> 缺 font-size（各浏览器默认值不一，会跳字）: %s' % el[:70])

    # ---- 6. 根元素必须带 font-family（中文才不至于糊）----
    root = raw[:raw.index('>') + 1]
    if 'font-family' not in root:
        notes.append('根 <svg> 没写 font-family，中文可能落到 serif 上')

    # ---- 7. 属性拼接事故：`x="y"/ fill=` 这种----
    if re.search(r'/\s+(fill|stroke|font-size)\s*=', raw):
        errs.append('属性拼接异常（`/` 出现在属性中间）—— 疑似字符串拼接 bug')

    # ---- 8. viewBox 必须存在（否则 GitHub 里尺寸会不可控）----
    if 'viewBox=' not in root:
        errs.append('根 <svg> 缺 viewBox —— GitHub 里宽度不可控')

    return errs, notes


def main():
    args = sys.argv[1:]
    files = args if args else sorted(glob.glob(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), '*.svg')))
    if not files:
        print('没有找到 .svg 文件')
        return 1

    bad = 0
    print('==== README 配图守卫（GitHub 渲染兼容性）====')
    for f in files:
        name = os.path.basename(f)
        errs, notes = check(f)
        size = len(open(f, 'rb').read())
        if errs:
            bad += 1
            print('  [FAIL] %s  (%d B)' % (name, size))
            for e in errs:
                print('         - ' + e)
        else:
            print('  [PASS] %s  (%d B)' % (name, size))
        for n in notes:
            print('         ~ ' + n)

    print('---- %d 张图，%d 张有问题 ----' % (len(files), bad))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
