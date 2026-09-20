# -*- coding: utf-8 -*-
"""UV 是真展开还是假展开？—— 局部连续性判据 + 随机打乱对照组。

原理：对每一条网格边，若 UV 是一个真展开，则 |ΔUV| / |ΔP| 在连通的图元内部
     基本恒定（= 全局 texel 密度），只有跨接缝的少数边才会很大。
     若 UV 是错位的（导出时丢了、或被打乱），这个比值会整体离散。
对照：把 UV 在顶点间随机置换后再算一次 —— 这才是「乱」的基线。
"""
import numpy as np, struct, json, io, os
from PIL import Image

GLB = r"D:\Obsidian_SecondBrain\SecondBrain\40 Projects\阿助娘化形象\model\chibi_maid_pet.glb"

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


def edge_stats(uv, tag):
    E = np.vstack([IDX[:, [0, 1]], IDX[:, [1, 2]], IDX[:, [2, 0]]])
    E = np.unique(np.sort(E, axis=1), axis=0)
    dP = np.linalg.norm(POS[E[:, 0]] - POS[E[:, 1]], axis=1)
    dU = np.linalg.norm(uv[E[:, 0]] - uv[E[:, 1]], axis=1)
    m = dP > 1e-9
    r = dU[m] / dP[m]
    med = np.median(r)
    print("  %-10s 边数=%d  |ΔUV|/|ΔP| 中位=%.4f  >4×中位占比=%.2f%%  >10×中位占比=%.2f%%  相关=%.3f"
          % (tag, m.sum(), med, 100.0 * (r > 4 * med).mean(), 100.0 * (r > 10 * med).mean(),
             np.corrcoef(dP[m], dU[m])[0, 1]))
    return r, med


print("=== 局部连续性 ===")
r_real, med_real = edge_stats(UV, "真实 UV")
rng = np.random.default_rng(7)
perm = rng.permutation(len(UV))
r_shuf, med_shuf = edge_stats(UV[perm], "打乱 UV")

print("\n真实 UV 的比值分布分位："
      + "  ".join("%d%%=%.4f" % (p, np.percentile(r_real, p)) for p in (1, 5, 25, 50, 75, 95, 99)))

# ---- 若真实 UV 中「连续」的边占绝大多数，说明展开是真的 ----
# 做法：按「3D 距离极小」的边单独看 —— 这些边两端几乎在同一点，UV 也必须几乎相同。
tiny = np.linalg.norm(POS[np.unique(np.sort(np.vstack(
    [IDX[:, [0, 1]], IDX[:, [1, 2]], IDX[:, [2, 0]]]), axis=1), axis=0)[:, 0]]
    - POS[np.unique(np.sort(np.vstack([IDX[:, [0, 1]], IDX[:, [1, 2]], IDX[:, [2, 0]]]),
                             axis=1), axis=0)[:, 1]], axis=1)
E = np.unique(np.sort(np.vstack([IDX[:, [0, 1]], IDX[:, [1, 2]], IDX[:, [2, 0]]]), axis=1), axis=0)
dP = np.linalg.norm(POS[E[:, 0]] - POS[E[:, 1]], axis=1)
dU = np.linalg.norm(UV[E[:, 0]] - UV[E[:, 1]], axis=1)
sel = dP < 1e-4                      # 几乎重合的顶点对
print("\n几乎重合的顶点对（|ΔP|<1e-4）：%d 条，其中 |ΔUV|>0.01 的占 %.1f%%"
      % (sel.sum(), 100.0 * (dU[sel] > 0.01).mean()))
