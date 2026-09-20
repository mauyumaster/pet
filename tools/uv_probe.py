# -*- coding: utf-8 -*-
"""把「贴图本身」和「UV 展开布局」直接画出来看。

上一轮我用「邻域相关性 AC1h=0.974」判定贴图不是噪声 —— 那个指标只能排除白噪声，
区分不了「一张正常的角色贴图」和「一张结构混乱的贴图」。现在能看图了，就直接看。
"""
import numpy as np, struct, json, io, os
from PIL import Image

GLB = r"D:\Obsidian_SecondBrain\SecondBrain\40 Projects\阿助娘化形象\model\chibi_maid_pet.glb"
OUT = r"D:\_petest\ref"
os.makedirs(OUT, exist_ok=True)

d = open(GLB, 'rb').read()
off, j, bin_ = 12, None, None
while off + 8 <= len(d):
    clen, ctype = struct.unpack_from('<II', d, off)
    blob = d[off + 8:off + 8 + clen]
    if ctype == 0x4E4F534A: j = json.loads(blob.decode('utf-8'))
    elif ctype == 0x004E4942: bin_ = blob
    off += 8 + clen


def read_acc(i):
    a = j['accessors'][i]
    bv = j['bufferViews'][a['bufferView']]
    dt = {5121: 'u1', 5123: 'u2', 5125: 'u4', 5126: 'f4'}[a['componentType']]
    n = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3}[a['type']]
    base = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    isz = np.dtype(dt).itemsize
    stride = bv.get('byteStride') or n * isz
    if stride == n * isz:
        return np.frombuffer(bin_, dtype='<' + dt, count=a['count'] * n, offset=base).reshape(-1, n)
    out = np.zeros((a['count'], n))
    for k in range(a['count']):
        out[k] = np.frombuffer(bin_, dtype='<' + dt, count=n, offset=base + k * stride)
    return out


POS = read_acc(0).astype(np.float64)
UV = read_acc(2).astype(np.float64)
IDX = read_acc(3).astype(np.int64).reshape(-1, 3)

# ---- ① 三张贴图都导出来看看 ----
for k, name in enumerate(['normal', 'whale', 'metalrough']):
    bv = j['bufferViews'][j['images'][k]['bufferView']]
    b = bv.get('byteOffset', 0)
    im = Image.open(io.BytesIO(bin_[b:b + bv['byteLength']]))
    print("image[%d] %-11s %s %s" % (k, name, im.size, im.mode))
    im.convert('RGB').resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, 'tex_%s.png' % name))
    im.convert('RGB').crop((0, 0, 512, 512)).save(os.path.join(OUT, 'tex_%s_tl.png' % name))

# ---- ② UV 展开布局：把所有三角形在 UV 空间画出来（白线黑底） ----
N = 1024
canvas = np.zeros((N, N), dtype=np.uint8)
for t in IDX:
    pts = UV[t] * (N - 1)
    for a, b in ((0, 1), (1, 2), (2, 0)):
        x0, y0 = pts[a]; x1, y1 = pts[b]
        steps = int(max(abs(x1 - x0), abs(y1 - y0))) + 1
        xs = np.linspace(x0, x1, steps).round().astype(int).clip(0, N - 1)
        ys = np.linspace(y0, y1, steps).round().astype(int).clip(0, N - 1)
        canvas[ys, xs] = 255
Image.fromarray(canvas).save(os.path.join(OUT, 'uv_layout.png'))
print("uv 线框图 ->", os.path.join(OUT, 'uv_layout.png'))

# ---- ③ UV 是否「有结构」的算术：三角形 UV 面积 vs 3D 面积 ----
p0, p1, p2 = POS[IDX[:, 0]], POS[IDX[:, 1]], POS[IDX[:, 2]]
a3 = 0.5 * np.linalg.norm(np.cross(p1 - p0, p2 - p0), axis=1)
u0, u1, u2 = UV[IDX[:, 0]], UV[IDX[:, 1]], UV[IDX[:, 2]]
a2 = 0.5 * np.abs((u1[:, 0] - u0[:, 0]) * (u2[:, 1] - u0[:, 1]) - (u2[:, 0] - u0[:, 0]) * (u1[:, 1] - u0[:, 1]))
ok = a3 > 1e-12
dens = a2[ok] / (a3[ok] / a3[ok].mean() * (1.0 / a3[ok].size))   # 归一化 texel 密度
print("\nUV 三角形总面积 = %.4f（>1 就说明 UV 重叠/翻折）" % a2.sum())
print("3D 表面积 = %.4f" % a3.sum())
print("texel 密度 归一化：中位 %.3f  1%% 分位 %.4f  99%% 分位 %.3f  最大 %.2f" %
      (np.median(dens), np.percentile(dens, 1), np.percentile(dens, 99), dens.max()))
print("面积为零的三角形 = %d / %d (%.3f%%)" % ((~ok).sum() + (a2 < 1e-12).sum(), len(a3), 100.0 * (a2 < 1e-12).mean()))
