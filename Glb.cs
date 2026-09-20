// 最小 glTF-binary 读取器 —— 只做我们这个模型用得到的部分，零外部依赖。
//
// 为什么自己写而不上 HelixToolkit/Assimp：
//   · 轻量是硬要求（HelixToolkit.Wpf.SharpDX 连带 SharpDX + native assimp 几十 MB）；
//   · 我们的 glb 结构极简（1 mesh / 1 primitive / 1 材质 / POSITION+NORMAL+TEXCOORD_0），
//     自己读反而**可控可断言** —— 顶点数、包围盒、贴图来源都能当场核。
//   · HelixToolkit.Wpf（不带 SharpDX 那版）**不支持 glTF**，只吃 OBJ/STL/3DS。
//
// glTF 与 WPF 3D 的坐标系：都是右手系、Y 向上、Z 朝观察者 ⇒ 顶点位置直接可用。
//
// ⚠⚠ 关于 V 到底翻不翻 —— 这里踩过一次大坑，结论是**不翻**：
//   流传很广的说法是「glTF 的 UV 原点在左上、WPF 在左下，所以 V 要翻」。那个说法
//   只在**单连通图集**（整张贴图就是一幅画）时看着像「上下颠倒」而无害；
//   本模型是**多图集**（2048² 里塞了上千个小块，每块对应身体的一小片）——
//   此时一个全局的 V 镜像会把每一块映射到**另一个身体部位的块**上，
//   于是不是颠倒，而是**整幅错位 ⇒ 渲染出来一片完全混乱**。
//   实测（2026-09-17）：翻 → 彩斑乱纹；不翻 → 与 three.js 参考图逐像素一致。
//   保留 VFlip 开关只为将来遇到真的需要翻的模型；默认 false。
//   判据用 `--vflip` 各渲一次看 `model_tex.png`，或用 tools/ref_render.py 软件光栅化对拍。
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace AzhuPet
{
    internal sealed class GlbModel
    {
        public MeshGeometry3D Mesh;
        public ImageSource BaseColor;
        public byte[] BaseColorBytes;      // 原始 PNG 字节：便于按不同分辨率重新解码（排错/降采样用）
        public string BaseColorName = "-";
        public int BaseColorW, BaseColorH;
        public Point3D Min, Max;            // 落地后（脚在 y=0、水平居中）的包围盒
        /// <summary>顶部 10% 身高这一段里 |x| 的最大值（米的半宽）。用于算「侧倾会把头顶抬多高」——
        /// ⚠ 别拿包围盒半宽代替：那按的是最宽处（手臂/臀部），会把抬升量高估好几倍。</summary>
        public double TopHalfWidth;
        /// <summary>顶部 10% 身高里 |z| 的最大值（半深）。用于算「前倾会把头顶抬多高」（打盹点头时）。</summary>
        public double TopHalfDepth;
        public int Vertices, Triangles, Images;
        public List<string> Notes = new List<string>();

        public double Height { get { return Max.Y - Min.Y; } }
        public double Width { get { return Max.X - Min.X; } }
        public double Depth { get { return Max.Z - Min.Z; } }
    }

    internal static class Glb
    {
        /// <summary>是否把 V 翻成 1-v。默认 **false**，理由见文件头的长注释：
        /// 多图集贴图一旦整体镜像，每块都会落到别的身体部位上，表现是「完全混乱」。
        /// 仅对「整张贴图就是一幅画」的模型才可能需要打开。</summary>
        public static bool VFlip;

        public static GlbModel Load(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 20) throw new InvalidDataException("文件太小，不是 glb");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0));
            if (magic != 0x46546C67u)
                throw new InvalidDataException("不是 glb：magic=0x" + magic.ToString("X8"));

            int off = 12, jsonOff = -1, jsonLen = 0, binOff = -1;
            while (off + 8 <= d.Length)
            {
                int clen = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off));
                uint ctype = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
                if (ctype == 0x4E4F534Au) { jsonOff = off + 8; jsonLen = clen; }
                else if (ctype == 0x004E4942u) { binOff = off + 8; }
                if (clen <= 0 || off + 8 + clen > d.Length) break;
                off += 8 + clen;
            }
            if (jsonOff < 0) throw new InvalidDataException("没有 JSON 块");
            if (binOff < 0) throw new InvalidDataException("没有 BIN 块");

            string json = Encoding.UTF8.GetString(d, jsonOff, jsonLen).TrimEnd('\0', ' ');
            var res = new GlbModel();
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                JsonElement accs = root.GetProperty("accessors");
                JsonElement bvs = root.GetProperty("bufferViews");
                JsonElement meshes = root.GetProperty("meshes");
                JsonElement prims = meshes[0].GetProperty("primitives");
                JsonElement prim = prims[0];
                if (prims.GetArrayLength() > 1)
                    res.Notes.Add("模型有 " + prims.GetArrayLength() + " 个 primitive，只取了第 1 个");

                JsonElement attrs = prim.GetProperty("attributes");
                int posAcc = attrs.GetProperty("POSITION").GetInt32();
                int nrmAcc = attrs.TryGetProperty("NORMAL", out JsonElement nv) ? nv.GetInt32() : -1;
                int uvAcc = attrs.TryGetProperty("TEXCOORD_0", out JsonElement uv) ? uv.GetInt32() : -1;
                int idxAcc = prim.TryGetProperty("indices", out JsonElement iv) ? iv.GetInt32() : -1;
                int matIdx = prim.TryGetProperty("material", out JsonElement mv) ? mv.GetInt32() : -1;
                if (prim.TryGetProperty("mode", out JsonElement md) && md.GetInt32() != 4)
                    res.Notes.Add("primitive.mode=" + md.GetInt32() + "（非三角形列表，未特殊处理）");

                float[] P = ReadFloat(accs[posAcc], accs, bvs, d, binOff);
                float[] N = nrmAcc >= 0 ? ReadFloat(accs[nrmAcc], accs, bvs, d, binOff) : null;
                float[] T = uvAcc >= 0 ? ReadFloat(accs[uvAcc], accs, bvs, d, binOff) : null;
                int[] I = idxAcc >= 0
                    ? ReadIndex(accs[idxAcc], accs, bvs, d, binOff)
                    : null;
                int vcount = accs[posAcc].GetProperty("count").GetInt32();

                // ---- 节点变换（本项目只有 translation；rotation/scale 也支持，matrix 走另一支）----
                double tx = 0, ty = 0, tz = 0;
                if (root.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement n in nodes.EnumerateArray())
                    {
                        if (!n.TryGetProperty("mesh", out JsonElement nm) || nm.GetInt32() != 0) continue;
                        if (n.TryGetProperty("matrix", out JsonElement _m))
                            res.Notes.Add("节点用了 matrix，本读取器只支持 TRS，已跳过矩阵");
                        if (n.TryGetProperty("translation", out JsonElement tr) && tr.GetArrayLength() == 3)
                        {
                            tx = tr[0].GetDouble(); ty = tr[1].GetDouble(); tz = tr[2].GetDouble();
                        }
                        if (n.TryGetProperty("rotation", out JsonElement _rot)) res.Notes.Add("节点带 rotation，未应用");
                        if (n.TryGetProperty("scale", out JsonElement _sc)) res.Notes.Add("节点带 scale，未应用");
                        break;
                    }
                }

                // ---- 落地：脚放 y=0、水平居中。之后「身高」就是干净的姿态幅度基准 ----
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                for (int i = 0; i < vcount; i++)
                {
                    double x = P[i * 3] + tx, y = P[i * 3 + 1] + ty, z = P[i * 3 + 2] + tz;
                    P[i * 3] = (float)x; P[i * 3 + 1] = (float)y; P[i * 3 + 2] = (float)z;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
                double ox = (minX + maxX) / 2, oy = minY, oz = (minZ + maxZ) / 2;

                var pos = new Point3D[vcount];
                var nor = N != null ? new Vector3D[vcount] : null;
                var tex = T != null ? new Point[vcount] : null;
                double height = maxY - minY, topBand = height * 0.90;   // y > topBand 的顶点 = 头顶那一小段
                for (int i = 0; i < vcount; i++)
                {
                    pos[i] = new Point3D(P[i * 3] - ox, P[i * 3 + 1] - oy, P[i * 3 + 2] - oz);
                    if (pos[i].Y > topBand)
                    {
                        res.TopHalfWidth = Math.Max(res.TopHalfWidth, Math.Abs(pos[i].X));
                        res.TopHalfDepth = Math.Max(res.TopHalfDepth, Math.Abs(pos[i].Z));
                    }
                    if (nor != null) nor[i] = new Vector3D(N[i * 3], N[i * 3 + 1], N[i * 3 + 2]);
                    // 默认原样送（V 不翻）；VFlip 只为兼容个别「单幅画」贴图而留。
                    if (tex != null) tex[i] = new Point(T[i * 2], VFlip ? 1.0 - T[i * 2 + 1] : T[i * 2 + 1]);
                }

                int triCount = I != null ? I.Length / 3 : vcount / 3;

                // 冻结顺序：先把每个集合冻上，再冻网格（父对象的 Freeze 不保证把子对象一起冻）
                var pcol = new Point3DCollection(pos); pcol.Freeze();
                var mesh = new MeshGeometry3D();
                mesh.Positions = pcol;
                if (nor != null) { var c = new Vector3DCollection(nor); c.Freeze(); mesh.Normals = c; }
                if (tex != null) { var c = new PointCollection(tex); c.Freeze(); mesh.TextureCoordinates = c; }
                if (I != null) { var c = new Int32Collection(I); c.Freeze(); mesh.TriangleIndices = c; }
                mesh.Freeze();

                res.Mesh = mesh;
                res.Vertices = vcount;
                res.Triangles = triCount;
                res.Min = new Point3D(-(maxX - minX) / 2, 0, -(maxZ - minZ) / 2);
                res.Max = new Point3D((maxX - minX) / 2, maxY - minY, (maxZ - minZ) / 2);

                // ---- 基础色贴图：material → pbrMetallicRoughness.baseColorTexture → textures → images ----
                if (root.TryGetProperty("images", out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
                    res.Images = imgs.GetArrayLength();
                if (matIdx >= 0 && root.TryGetProperty("materials", out JsonElement mats))
                {
                    JsonElement mat = mats[matIdx];
                    if (mat.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr)
                        && pbr.TryGetProperty("baseColorTexture", out JsonElement bct))
                    {
                        int texIdx = bct.GetProperty("index").GetInt32();
                        if (bct.TryGetProperty("texCoord", out JsonElement tc) && tc.GetInt32() != 0)
                            res.Notes.Add("baseColorTexture 用了 texCoord=" + tc.GetInt32() + "，本读取器只用 TEXCOORD_0");
                        JsonElement textures = root.GetProperty("textures");
                        int srcIdx = textures[texIdx].GetProperty("source").GetInt32();
                        JsonElement img = imgs[srcIdx];
                        res.BaseColorName = img.TryGetProperty("name", out JsonElement inm) ? inm.GetString() : ("image" + srcIdx);
                        if (img.TryGetProperty("bufferView", out JsonElement ibv))
                        {
                            JsonElement bv = bvs[ibv.GetInt32()];
                            int bo = bv.TryGetProperty("byteOffset", out JsonElement boe) ? boe.GetInt32() : 0;
                            int bl = bv.GetProperty("byteLength").GetInt32();
                            var bytes = new byte[bl];
                            Array.Copy(d, binOff + bo, bytes, 0, bl);
                            res.BaseColorBytes = bytes;
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = new MemoryStream(bytes, false);
                            bmp.EndInit();
                            bmp.Freeze();
                            res.BaseColor = bmp;
                            res.BaseColorW = bmp.PixelWidth;
                            res.BaseColorH = bmp.PixelHeight;
                        }
                        else res.Notes.Add("贴图不是内嵌 bufferView（可能是外部 uri），未加载");
                    }
                    else res.Notes.Add("材质没有 baseColorTexture");
                }
                else res.Notes.Add("没有可用的材质");
            }
            return res;
        }

        // ---------------------------------------------------------------- 访问器读取
        private static int Comps(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                default: throw new InvalidDataException("未知 accessor.type=" + type);
            }
        }

        private static float[] ReadFloat(JsonElement acc, JsonElement accs, JsonElement bvs, byte[] d, int binOff)
        {
            int count = acc.GetProperty("count").GetInt32();
            int n = Comps(acc.GetProperty("type").GetString());
            int baseOff = BaseOffset(acc, bvs, binOff);
            int stride = Stride(acc, bvs, n, 4);
            var r = new float[count * n];
            for (int i = 0; i < count; i++)
            {
                int p = baseOff + i * stride;
                for (int c = 0; c < n; c++)
                    r[i * n + c] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p + c * 4));
            }
            return r;
        }

        private static int[] ReadIndex(JsonElement acc, JsonElement accs, JsonElement bvs, byte[] d, int binOff)
        {
            int count = acc.GetProperty("count").GetInt32();
            int ct = acc.GetProperty("componentType").GetInt32();
            int sz = ct == 5121 ? 1 : ct == 5123 ? 2 : 4;
            int baseOff = BaseOffset(acc, bvs, binOff);
            int stride = Stride(acc, bvs, 1, sz);
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                int p = baseOff + i * stride;
                if (ct == 5121) r[i] = d[p];
                else if (ct == 5123) r[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
                else if (ct == 5125) r[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
                else throw new InvalidDataException("索引 componentType=" + ct + " 不支持");
            }
            return r;
        }

        private static int BaseOffset(JsonElement acc, JsonElement bvs, int binOff)
        {
            int bvIdx = acc.GetProperty("bufferView").GetInt32();
            JsonElement bv = bvs[bvIdx];
            int bo = bv.TryGetProperty("byteOffset", out JsonElement e) ? e.GetInt32() : 0;
            int ao = acc.TryGetProperty("byteOffset", out JsonElement e2) ? e2.GetInt32() : 0;
            return binOff + bo + ao;
        }

        private static int Stride(JsonElement acc, JsonElement bvs, int n, int compSize)
        {
            int bvIdx = acc.GetProperty("bufferView").GetInt32();
            JsonElement bv = bvs[bvIdx];
            if (bv.TryGetProperty("byteStride", out JsonElement s)) return s.GetInt32();
            return n * compSize;
        }
    }
}
