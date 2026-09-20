# -*- coding: utf-8 -*-
"""跨渲染器同框像素对照：WPF 的截图 vs 软件光栅化参考图。

这是本项目第一次能把「渲染对不对」变成一个数。两个渲染器完全独立
（一个走 WPF 的 Viewport3D / D3D，一个是 numpy 自己光栅化），
只要它们在同一套几何/UV/贴图/相机下给出同一张图，就说明「读对了 + 贴对了」。
"""
import numpy as np
from PIL import Image
import sys

WPF = sys.argv[1] if len(sys.argv) > 1 else r"D:\_petest\fix\model_tex.png"
REF = sys.argv[2] if len(sys.argv) > 2 else r"D:\_petest\ref\ref_tex_noflip.png"
MSK = r"D:\_petest\ref\mask.npy"

a = np.asarray(Image.open(WPF).convert('RGB')).astype(np.float64)
b = np.asarray(Image.open(REF).convert('RGB')).astype(np.float64)
m = np.load(MSK)
print("WPF %s  参考 %s  掩码 %d px" % (a.shape, b.shape, m.sum()))
assert a.shape == b.shape, "尺寸不一致"

# 参考图自己的底是 (240,240,238)；WPF 的 WriteModelOnly 也是浅底 —— 用掩码排除底
d = np.abs(a - b).mean(axis=2)
print("\n=== 全图（含底） ===")
print("平均绝对差 %.3f  最大 %.0f" % (d.mean(), d.max()))
print("\n=== 只算模型像素 ===")
dv = d[m]
print("平均绝对差 %.3f  中位 %.3f  90%%分位 %.3f  最大 %.0f" % (dv.mean(), np.median(dv), np.percentile(dv, 90), dv.max()))
for th in (8, 16, 32, 64):
    print("  差 > %-3d 的像素占 %.2f%%" % (th, 100.0 * (dv > th).mean()))

# 相关性（逐通道）
print("\n=== 逐通道相关（模型像素） ===")
for i, c in enumerate('RGB'):
    x = a[:, :, i][m]; y = b[:, :, i][m]
    print("  %s: 相关 %.4f   均值 WPF %.1f / 参考 %.1f" % (c, np.corrcoef(x, y)[0, 1], x.mean(), y.mean()))

# 削波：两边各有多少像素贴顶
clipA = ((a[m] >= 254).all(axis=1)).mean()
clipB = ((b[m] >= 254).all(axis=1)).mean()
print("\n=== 过曝 ===")
print("  三通道到顶的像素：WPF %.2f%%  参考 %.2f%%" % (100 * clipA, 100 * clipB))
anyA = ((a[m] >= 254).any(axis=1)).mean(); anyB = ((b[m] >= 254).any(axis=1)).mean()
print("  任一通道到顶：  WPF %.2f%%  参考 %.2f%%" % (100 * anyA, 100 * anyB))

# 差异图（放大 2 倍便于看）
dimg = np.clip(d * 3, 0, 255).astype(np.uint8)
Image.fromarray(dimg).save(r"D:\_petest\fix\diff_map.png")
print("\n差异图 -> D:\\_petest\\fix\\diff_map.png")
