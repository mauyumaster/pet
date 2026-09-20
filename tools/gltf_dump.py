# -*- coding: utf-8 -*-
"""把 glb 的 JSON 结构完整摊开：primitive / attribute / accessor / material / texture。
目的是回答一个具体问题：贴图到底该按哪个 UV 通道、有没有 texture_transform、
             一个 mesh 里有几个 primitive、每个 primitive 用哪个 material。
"""
import json, struct, sys, os

p = sys.argv[1] if len(sys.argv) > 1 else \
    r"D:\Obsidian_SecondBrain\SecondBrain\40 Projects\阿助娘化形象\model\chibi_maid_pet.glb"

d = open(p, 'rb').read()
magic, ver, length = struct.unpack_from('<III', d, 0)
print("=== 容器 ===")
print("magic=%r version=%d header_len=%d file_len=%d" % (d[:4], ver, length, len(d)))
off, chunks = 12, []
while off < len(d):
    clen, ctype = struct.unpack_from('<II', d, off)
    chunks.append((ctype, off + 8, clen))
    off += 8 + clen
for ctype, o, l in chunks:
    print("  chunk type=%s off=%d len=%d" % (ctype.to_bytes(4, 'little'), o, l))

j = json.loads(d[chunks[0][1]:chunks[0][1] + chunks[0][2]].decode('utf-8'))

print("\n=== asset ===")
print(json.dumps(j.get('asset', {}), ensure_ascii=False, indent=1))
print("extensionsUsed:", j.get('extensionsUsed'))
print("extensionsRequired:", j.get('extensionsRequired'))

print("\n=== scenes / nodes ===")
print("scenes:", json.dumps(j.get('scenes', []), ensure_ascii=False))
for i, n in enumerate(j.get('nodes', [])):
    print("  node[%d] name=%r mesh=%s skin=%s children=%s matrix=%s translation=%s rotation=%s scale=%s"
          % (i, n.get('name'), n.get('mesh'), n.get('skin'), n.get('children'),
             n.get('matrix'), n.get('translation'), n.get('rotation'), n.get('scale')))

print("\n=== meshes / primitives ===")
for i, m in enumerate(j.get('meshes', [])):
    print("mesh[%d] name=%r prims=%d weights=%s" % (i, m.get('name'), len(m.get('primitives', [])), m.get('weights')))
    for k, pr in enumerate(m['primitives']):
        print("  prim[%d] mode=%s material=%s indices=%s" % (k, pr.get('mode'), pr.get('material'), pr.get('indices')))
        for an, av in pr['attributes'].items():
            print("      attr %-14s accessor=%d" % (an, av))
        for ext in (pr.get('extensions') or {}):
            print("      prim ext:", ext)

print("\n=== accessors ===")
for i, a in enumerate(j.get('accessors', [])):
    print("  acc[%d] type=%-6s comp=%-5s count=%-7s normalized=%s byteOffset=%-6s bufferView=%s min=%s max=%s name=%r"
          % (i, a.get('type'), a.get('componentType'), a.get('count'), a.get('normalized'),
             a.get('byteOffset', 0), a.get('bufferView'),
             [round(v, 4) for v in a['min']] if 'min' in a else None,
             [round(v, 4) for v in a['max']] if 'max' in a else None,
             a.get('name')))

print("\n=== bufferViews ===")
for i, bv in enumerate(j.get('bufferViews', [])):
    print("  bv[%d] buffer=%s byteOffset=%-9s byteLength=%-9s byteStride=%s target=%s name=%r"
          % (i, bv.get('buffer'), bv.get('byteOffset', 0), bv.get('byteLength'),
             bv.get('byteStride'), bv.get('target'), bv.get('name')))

print("\n=== materials ===")
print(json.dumps(j.get('materials', []), ensure_ascii=False, indent=1))

print("\n=== textures / samplers / images ===")
print(json.dumps(j.get('textures', []), ensure_ascii=False, indent=1))
print(json.dumps(j.get('samplers', []), ensure_ascii=False, indent=1))
for i, im in enumerate(j.get('images', [])):
    print("  image[%d] name=%r mime=%s bufferView=%s uri=%s"
          % (i, im.get('name'), im.get('mimeType'), im.get('bufferView'), im.get('uri')))

print("\n=== skins / animations ===")
print("skins:", len(j.get('skins', [])), "animations:", len(j.get('animations', [])))
for i, s in enumerate(j.get('skins', [])):
    print("  skin[%d] name=%r joints=%d" % (i, s.get('name'), len(s.get('joints', []))))
