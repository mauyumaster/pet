#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SVG 排版自检 —— 量算每段文字的包围盒，抓「看不清但看得见」的病。

WHY 需要它：渲染兼容性判据（check-readme-svgs.py）只保证「GitHub 不会剥样式」。
它不管**画出来好不好看** —— 文字压在一起、跑出画布、叠在色块外，它全都放行。
这类问题截图能看出来，但要么靠我目视（不可靠）、要么得开浏览器（慢且会挂）。

办法：把 <text> 的字宽按「中文＝1em、西文/数字≈0.55em」估出来，得到包围盒，
然后查三件事：
  1. 出界   —— 盒子超出 viewBox
  2. 自身压 —— 同一行里两个 text 水平距离过近
  3. 撑出色块 —— 一段文字的起点落在某个小色块里，盒子却从色块右边伸出去

⚠ 第 3 条是 2026-09-26 补的，而且**是被一次真实的漏网逼出来的**：原来的脚本只查
「有没有出画布」，于是「文字长出色块」这类排版事故它一律放行 —— 图能画出来、守卫全绿、
线上看着就是文字压出边框。这和本项目在打包脚本上踩的是同一个坑：**判据太松＝没有判据**。
补的时候顺手做了负对照（把一行文案人为加长，确认它真的会红）。

⚠ 尚未实现：文档早期声称查「对比度不足」，代码里其实只有上面三条。别拿文档当判据。

这是**估算**，不是精确渲染度量。所以阈值留了余量，报出来的要人工确认。
用法：python check-svg-layout.py [file.svg ...]
"""
import sys, os, re, glob
import xml.etree.ElementTree as ET

NS = '{http://www.w3.org/2000/svg}'

# 估算系数（相对 font-size）：中日韩全角 ~1.0，西文/数字/常见符号 ~0.55
CJK = re.compile(r'[\u2e80-\u9fff\uff00-\uffef\u3000-\u303f]')


def est_width(text, fs):
    w = 0.0
    for ch in text:
        w += 1.0 if CJK.match(ch) else 0.55
    return w * fs


def parse_attrs(el):
    return el.attrib


def alpha_of(color):
    """返回 (r,g,b)。"""
    if not color:
        return None
    c = color.strip().lower()
    if c.startswith('#'):
        h = c[1:]
        if len(h) == 3:
            h = ''.join(ch * 2 for ch in h)
        try:
            return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
        except ValueError:
            return None
    m = re.match(r'rgba?\(([^)]+)\)', c)
    if m:
        parts = [p.strip() for p in m.group(1).split(',')]
        try:
            return tuple(int(float(p)) for p in parts[:3])
        except ValueError:
            return None
    return None


def lum(rgb):
    if not rgb:
        return None
    r, g, b = [v / 255.0 for v in rgb]
    f = lambda c: c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)


def contrast(a, b):
    la, lb = lum(a), lum(b)
    if la is None or lb is None:
        return None
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def bounds_of(svg_w, svg_h, texts, rects):
    issues = []
    boxes = []
    for t in texts:
        try:
            x = float(t.get('x', 0)); y = float(t.get('y', 0))
            fs = float(re.sub(r'[^0-9.]', '', t.get('font-size', '12')) or 12)
        except ValueError:
            continue
        body = ''.join(t.itertext())
        w = est_width(body, fs)
        anchor = (t.get('text-anchor') or 'start').strip()
        if anchor == 'middle':
            x0 = x - w / 2
        elif anchor == 'end':
            x0 = x - w
        else:
            x0 = x
        # y 是基线；字高约 0.75em 在上、0.25em 在下
        boxes.append({'x0': x0, 'x1': x0 + w, 'y0': y - fs * 0.78, 'y1': y + fs * 0.25,
                      'fs': fs, 'text': body, 'fill': t.get('fill'),
                      'ox': x, 'oy': y})

    # 1. 出界
    for b in boxes:
        if b['x0'] < -1 or b['x1'] > svg_w + 1 or b['y0'] < -1 or b['y1'] > svg_h + 1:
            issues.append(('出界', '「%s」 x=[%.0f,%.0f] y=[%.0f,%.0f] 超出 %gx%g'
                           % (b['text'][:20], b['x0'], b['x1'], b['y0'], b['y1'], svg_w, svg_h)))

    # 2. 同一行水平重叠（两个 text 明显压在一起）
    for i in range(len(boxes)):
        for j in range(i + 1, len(boxes)):
            a, c = boxes[i], boxes[j]
            vover = min(a['y1'], c['y1']) - max(a['y0'], c['y0'])
            if vover <= min(a['fs'], c['fs']) * 0.5:
                continue
            hgap = max(a['x0'], c['x0']) - min(a['x1'], c['x1'])
            if hgap < -2:   # 真的叠上了
                issues.append(('重叠', '「%s」 与 「%s」 水平重叠 %.0fpx'
                               % (a['text'][:16], c['text'][:16], -hgap)))
    return boxes, issues


def containment_issues(rects, boxes, max_host_w=600):
    """3. 撑出色块 —— 文字起点在某个色块里，盒子却从它右边伸出去。

    WHY 要有这一条：只查「出画布」拦不住最常见的排版事故。一段文案被改长之后，
    画布还是装得下，色块却装不下了 —— 渲染出来就是文字压在边框上。
    GitHub 的渲染守卫（check-readme-svgs.py）也不管这个：它只管颜色会不会被剥掉。

    只把**小色块**当承载容器（宽度 <= max_host_w）：外层白色底板本来就应该包住一整片，
    拿它当容器等于什么都没查。
    """
    issues = []
    hosts = []
    for r in rects:
        try:
            rx = float(r.get('x', 0)); ry = float(r.get('y', 0))
            rw = float(r.get('width', 0)); rh = float(r.get('height', 0))
        except ValueError:
            continue
        if rw <= 0 or rh <= 0 or rw > max_host_w:
            continue
        hosts.append((rx, ry, rw, rh))

    for b in boxes:
        ox, oy = b.get('ox'), b.get('oy')
        if ox is None or oy is None:
            continue
        # 起点**深入**落在哪个小色块里，那个色块才算它的「家」。
        #
        # ⚠ 留 2px 边距不是洁癖，是实测逼出来的：privacy-boundary.svg 里有一句
        # 居中的「分界线」标签，起点 x 恰好等于左边那个面板的右边界（424 == 424）。
        # 按「<=」判定，这句**刻意压在分隔线上**的标签会被算成「从面板里伸出来 15px」，
        # 报出一个假阳性。判据放着假阳性不管，下次真出问题时就没人信它了。
        host = None
        for (rx, ry, rw, rh) in hosts:
            if rx + 2 <= ox <= rx + rw - 2 and ry + 2 <= oy <= ry + rh - 2:
                host = (rx, ry, rw, rh)
        if host is None:
            continue        # 不在任何小色块**内部**（标题、压边界的标签、自由文字），不适用
        rx, ry, rw, rh = host
        over = max(b['x1'] - (rx + rw), rx - b['x0'])
        if over > 0.5:
            side = '右' if b['x1'] > rx + rw else '左'
            issues.append(('撑出', '「%s」 从色块%s边伸出 %.0fpx（色块 x=[%.0f,%.0f]，文字 x=[%.0f,%.0f]）'
                           % (b['text'][:16], side, over, rx, rx + rw, b['x0'], b['x1'])))
    return issues


def main():
    args = sys.argv[1:]
    files = args if args else sorted(glob.glob(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), '*.svg')))
    if not files:
        print('没有 .svg')
        return 1
    bad = 0
    print('==== SVG 排版自检（包围盒量算）====')
    for f in files:
        name = os.path.basename(f)
        tree = ET.parse(f)
        root = tree.getroot()
        vb = (root.get('viewBox') or '').split()
        svg_w = float(vb[2]) if len(vb) == 4 else float(root.get('width', 720))
        svg_h = float(vb[3]) if len(vb) == 4 else float(root.get('height', 400))

        texts = [e for e in root.iter() if e.tag.endswith('}text')]
        rects = [e for e in root.iter() if e.tag.endswith('}rect')]

        boxes, issues = bounds_of(svg_w, svg_h, texts, rects)
        issues += containment_issues(rects, boxes)
        print('  %s  —— %d 段文字, 画布 %gx%g' % (name, len(texts), svg_w, svg_h))
        if issues:
            bad += 1
            for kind, msg in issues:
                print('     [%s] %s' % (kind, msg))
        else:
            print('     [PASS] 无出界、无重叠、无撑出色块')
    print('---- %d 张图，%d 张有排版问题 ----' % (len(files), bad))
    print('注：字宽为估算（中文 1em／西文 0.55em），报出来的请人工确认。')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
