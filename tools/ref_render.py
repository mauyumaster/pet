# -*- coding: utf-8 -*-
"""软件光栅化参考渲染器。

存在的唯一理由：WPF 那边的贴图映射不对，而我不能凭猜。这个脚本用**同一份 glb 的
同一批顶点/UV/索引**，自己把模型画出来 —— 它就是「本该长什么样」的真值。
然后跟 WPF 的截图逐像素比，就能把「读错了」和「贴错了」分开。

坐标系与相机严格照抄 Renderer.cs：
  · 落地：x -= (minX+maxX)/2, y -= minY, z -= (minZ+maxZ)/2
  · fov_v = 30°，MarginY=1.30，MarginX=1.40
  · dist = max(distV, distH)，相机 (0, lookY+mh*0.045, dist) 看向 (0, lookY, 0)
光照也照抄：amb(152,152,152) + key(192,192,192)，方向 (-1.5,-2.7,-2.3) 归一化，
线性空间相乘（WPF 是 scRGB 线性照明）。
"""
import numpy as np, struct, json, os, sys
from PIL import Image

GLB = r"D:\Obsidian_SecondBrain\SecondBrain\40 Projects\阿助娘化形象\model\chibi_maid_pet.glb"
OUT = r"D:\_petest\ref"
W, H = 480, 600                      # 与 WPF 截图同尺寸（设备像素）
BACKDROP = (240, 240, 238)           # WriteModelOnly 用的浅底


# ------------------------------------------------------------------ glb 解析
def load_glb(path):
    d = open(path, 'rb').read()
    off, j, bin_ = 12, None, None
    while off + 8 <= len(d):
        clen, ctype = struct.unpack_from('<II', d, off)
        blob = d[off + 8:off + 8 + clen]
        if ctype == 0x4E4F534A:
            j = json.loads(blob.decode('utf-8'))
        elif ctype == 0x004E4942:
            bin_ = blob
        off += 8 + clen
    return j, bin_


JP, BIN = load_glb(GLB)
PRIM = JP['meshes'][0]['primitives'][0]


def read_acc(i):
    a = JP['accessors'][i]
    bv = JP['bufferViews'][a['bufferView']]
    dt = {5120: 'i1', 5121: 'u1', 5122: 'i2', 5123: 'u2', 5125: 'u4', 5126: 'f4'}[a['componentType']]
    n = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}[a['type']]
    base = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    isz = np.dtype(dt).itemsize
    stride = bv.get('byteStride') or n * isz
    if stride == n * isz:
        return np.frombuffer(BIN, dtype='<' + dt, count=a['count'] * n, offset=base).reshape(-1, n).astype(np.float64 if dt == 'f4' else np.int64)
    out = np.zeros((a['count'], n))
    for k in range(a['count']):
        out[k] = np.frombuffer(BIN, dtype='<' + dt, count=n, offset=base + k * stride)
    return out


POS = read_acc(PRIM['attributes']['POSITION']).astype(np.float64)
NRM = read_acc(PRIM['attributes']['NORMAL']).astype(np.float64)
UV = read_acc(PRIM['attributes']['TEXCOORD_0']).astype(np.float64)
IDX = read_acc(PRIM['indices']).astype(np.int64).reshape(-1)

# 节点平移
tr = JP['nodes'][0].get('translation', [0, 0, 0])
POS = POS + np.array(tr, dtype=np.float64)

# 落地 + 水平居中
mn = POS.min(0); mx = POS.max(0)
POS = POS - np.array([(mn[0] + mx[0]) / 2, mn[1], (mn[2] + mx[2]) / 2])
MN, MX = POS.min(0), POS.max(0)
MH, MW, MD = MX[1] - MN[1], MX[0] - MN[0], MX[2] - MN[2]
print("顶点 %d  三角 %d  身高 %.4f 宽 %.4f 深 %.4f" % (len(POS), len(IDX) // 3, MH, MW, MD))
print("UV 范围 u[%.4f,%.4f] v[%.4f,%.4f]" % (UV[:, 0].min(), UV[:, 0].max(), UV[:, 1].min(), UV[:, 1].max()))

# ------------------------------------------------------------------ 相机（照抄）
VFOV = np.radians(30.0)
tan_h = np.tan(VFOV / 2) * (W / H)          # WPF 的 FieldOfView 是水平角
tan_v = np.tan(VFOV / 2)
distV = (MH * 1.30 / 2) / tan_v
distH = (MW * 1.40 / 2) / tan_h
DIST = max(distV, distH)
LOOKY = MH * 0.5
CAM = np.array([0.0, LOOKY + MH * 0.045, DIST])
FWD = np.array([0.0, LOOKY, 0.0]) - CAM
FWD /= np.linalg.norm(FWD)
RIGHT = np.cross(FWD, np.array([0.0, 1.0, 0.0])); RIGHT /= np.linalg.norm(RIGHT)
UP = np.cross(RIGHT, FWD)
print("dist=%.4f cam=(%.4f,%.4f,%.4f) tan_h=%.4f tan_v=%.4f" % (DIST, CAM[0], CAM[1], CAM[2], tan_h, tan_v))

rel = POS - CAM
VX = rel @ RIGHT; VY = rel @ UP; VZ = rel @ FWD
SX = W / 2 + (W / 2) * (VX / VZ) / tan_h
SY = H / 2 - (H / 2) * (VY / VZ) / tan_v

# ------------------------------------------------------------------ 贴图
WHALE_BV = JP['bufferViews'][JP['images'][1]['bufferView']]
wb = WHALE_BV.get('byteOffset', 0)
whale = np.asarray(Image.open(__import__('io').BytesIO(BIN[wb:wb + WHALE_BV['byteLength']])).convert('RGB')).astype(np.float64)
TH, TW = whale.shape[:2]
print("whale 贴图 %dx%d" % (TW, TH))

# UV 渐变图（与 Renderer.cs 的 UvDiag 逐字节一致：B=0, G=y, R=x）
N = 256
diag = np.zeros((N, N, 3), dtype=np.float64)
diag[:, :, 0] = np.arange(N)[None, :] / (N - 1) * 255.0
diag[:, :, 1] = np.arange(N)[:, None] / (N - 1) * 255.0

# ------------------------------------------------------------------ 光栅化
def raster(uv_flip, res_scale=1.0):
    """uv_flip: True → 送进 TextureCoordinates 的是 (u, 1-v)；False → (u, v)。
    返回 (rgb, depth, mask)"""
    w = int(W * res_scale); h = int(H * res_scale)
    sx = SX * res_scale; sy = SY * res_scale
    tuv = np.stack([UV[:, 0], 1.0 - UV[:, 1] if uv_flip else UV[:, 1]], axis=1)

    zbuf = np.zeros((h, w), dtype=np.float64)     # 存 1/z（越大越近）
    uvbuf = np.zeros((h, w, 2), dtype=np.float64)
    nbuf = np.zeros((h, w, 3), dtype=np.float64)
    mask = np.zeros((h, w), dtype=bool)
    tri = IDX.reshape(-1, 3)
    inv = 1.0 / VZ

    for t in range(len(tri)):
        i0, i1, i2 = tri[t]
        x0, y0 = sx[i0], sy[i0]; x1, y1 = sx[i1], sy[i1]; x2, y2 = sx[i2], sy[i2]
        xlo = int(np.floor(min(x0, x1, x2))); xhi = int(np.ceil(max(x0, x1, x2)))
        ylo = int(np.floor(min(y0, y1, y2))); yhi = int(np.ceil(max(y0, y1, y2)))
        if xlo < 0: xlo = 0
        if ylo < 0: ylo = 0
        if xhi > w - 1: xhi = w - 1
        if yhi > h - 1: yhi = h - 1
        if xlo > xhi or ylo > yhi: continue
        det = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(det) < 1e-12: continue
        xs = np.arange(xlo, xhi + 1) + 0.5
        ys = np.arange(ylo, yhi + 1) + 0.5
        X, Y = np.meshgrid(xs, ys)
        l0 = ((y1 - y2) * (X - x2) + (x2 - x1) * (Y - y2)) / det
        l1 = ((y2 - y0) * (X - x2) + (x0 - x2) * (Y - y2)) / det
        l2 = 1.0 - l0 - l1
        inside = (l0 >= -1e-9) & (l1 >= -1e-9) & (l2 >= -1e-9)
        if not inside.any(): continue
        za, z1_, z2_ = inv[i0], inv[i1], inv[i2]
        zsum = l0 * za + l1 * z1_ + l2 * z2_
        # 只对 inside 的格子做深度测试（就地取索引，不要为每个三角形开大数组）
        yy, xx = np.nonzero(inside)
        yy = yy + ylo; xx = xx + xlo
        zz = zsum[inside]
        closer = zz > zbuf[yy, xx]
        if not closer.any(): continue
        yy = yy[closer]; xx = xx[closer]
        ll0 = l0[inside][closer]; ll1 = l1[inside][closer]; ll2 = l2[inside][closer]
        zs = zsum[inside][closer]
        zbuf[yy, xx] = zs
        mask[yy, xx] = True
        for c in range(2):
            uvbuf[yy, xx, c] = (ll0 * tuv[i0, c] * za + ll1 * tuv[i1, c] * z1_ + ll2 * tuv[i2, c] * z2_) / zs
        for c in range(3):
            nbuf[yy, xx, c] = (ll0 * NRM[i0, c] * za + ll1 * NRM[i1, c] * z1_ + ll2 * NRM[i2, c] * z2_) / zs
    return uvbuf, nbuf, mask


def srgb2lin(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lin2srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def shade(nbuf, mask, texel_srgb):
    n = nbuf.copy()
    ln = np.linalg.norm(n, axis=2, keepdims=True); ln[ln == 0] = 1
    n /= ln
    kd = np.array([-1.5, -2.7, -2.3]); kd = kd / np.linalg.norm(kd)
    lam = np.clip(-(n @ kd), 0, None)
    amb = srgb2lin(np.array([152, 152, 152]) / 255.0)
    key = srgb2lin(np.array([192, 192, 192]) / 255.0)
    light = amb[None, None, :] + key[None, None, :] * lam[:, :, None]
    out = srgb2lin(texel_srgb / 255.0) * light
    return lin2srgb(out) * 255.0


def sample(uvbuf, tex):
    th, tw = tex.shape[:2]
    x = np.clip((uvbuf[:, :, 0] % 1.0) * (tw - 1), 0, tw - 1)
    y = np.clip(((1 - (uvbuf[:, :, 1] % 1.0)) if False else (uvbuf[:, :, 1] % 1.0)) * (th - 1), 0, th - 1)
    xi = np.clip(np.round(x).astype(int), 0, tw - 1)
    yi = np.clip(np.round(y).astype(int), 0, th - 1)
    return tex[yi, xi]


def save(path, img, mask):
    a = np.zeros((img.shape[0], img.shape[1], 3), dtype=np.uint8)
    a[:, :] = BACKDROP
    a[mask] = np.clip(img[mask], 0, 255).astype(np.uint8)
    Image.fromarray(a).save(path)
    print("  ->", path)


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    for flip in (True, False):
        tag = "flip" if flip else "noflip"
        uvbuf, nbuf, mask = raster(flip)
        print("flip=%s  覆盖 %d px (%.1f%%)" % (tag, mask.sum(), 100.0 * mask.mean()))
        # UV 诊断图：贴图里 R=u、G=v ⇒ 采样它得到的就是 UV 本身
        save(os.path.join(OUT, "ref_uv_%s.png" % tag), sample(uvbuf, diag), mask)
        save(os.path.join(OUT, "ref_tex_%s.png" % tag), shade(nbuf, mask, sample(uvbuf, whale)), mask)
        np.save(os.path.join(OUT, "uvbuf_%s.npy" % tag), uvbuf.astype(np.float32))
        np.save(os.path.join(OUT, "mask.npy"), mask)
