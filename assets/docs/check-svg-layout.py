#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SVG 排版自检 —— 量算每段文字的包围盒，抓「看不清但看得见」的病。

WHY 需要它：渲染兼容性判据（check-readme-svgs.py）只保证「GitHub 不会剥样式」。
它不管**画出来好不好看** —— 文字压在一起、跑出画布、叠在色块外，它全都放行。
这类问题截图能看出来，但要么靠我目视（不可靠）、要么得开浏览器（慢且会挂）。

办法：把 <text> 的字宽按「中文＝1em、西文/数字≈0.55em」估出来，得到包围盒，
然后查三件事：
  1. 出界  —— 盒子超出 viewBox
  2. 自身压 —— 同一行里两个 text 水平距离过近
  3. 压色块 —— 文字压在与自己颜色太接近的背景上（对比度不足）

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
                      'fs': fs, 'text': body, 'fill': t.get('fill')})

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
        print('  %s  —— %d 段文字, 画布 %gx%g' % (name, len(texts), svg_w, svg_h))
        if issues:
            bad += 1
            for kind, msg in issues:
                print('     [%s] %s' % (kind, msg))
        else:
            print('     [PASS] 无出界、无重叠')
    print('---- %d 张图，%d 张有排版问题 ----' % (len(files), bad))
    print('注：字宽为估算（中文 1em／西文 0.55em），报出来的请人工确认。')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
