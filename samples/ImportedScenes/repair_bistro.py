"""Repair invalid tangent vectors in the downloaded GLB, retaining the original.

Only invalid vectors are changed. Zero tangents on degenerate UVs have no unique
solution; use a unit vector orthogonal to the source normal, retaining handedness.
All geometry, materials, textures, nodes and valid tangents are preserved.
"""
import hashlib
import json
import math
import pathlib
import struct
import shutil

root = pathlib.Path(__file__).parent
source = root / "Sources/Bistro/Bistro.glb"
data = bytearray(source.read_bytes())
assert hashlib.sha256(data).hexdigest() == "5cc23d52b95fa1ba8b585d998185904becf8bcaa8f424fd74cf6950addb63deb"
json_size = struct.unpack_from("<I", data, 12)[0]
model = json.loads(data[20:20 + json_size])
binary_start = 28 + json_size

def location(index, width):
    accessor = model["accessors"][index]
    view = model["bufferViews"][accessor["bufferView"]]
    assert accessor["componentType"] == 5126 and "sparse" not in accessor
    return (binary_start + view.get("byteOffset", 0) + accessor.get("byteOffset", 0),
            view.get("byteStride", width * 4), accessor["count"])

changes = []
visited = set()
for mesh in model["meshes"]:
    for primitive in mesh["primitives"]:
        attributes = primitive["attributes"]
        index = attributes.get("TANGENT")
        if index is None or index in visited:
            continue
        visited.add(index)
        start, stride, count = location(index, 4)
        normal_start, normal_stride, _ = location(attributes["NORMAL"], 3)
        changed = 0
        for vertex in range(count):
            tangent = struct.unpack_from("<4f", data, start + vertex * stride)
            length = math.sqrt(sum(x * x for x in tangent[:3]))
            if abs(length - 1) < 0.0001 and abs(tangent[3]) == 1:
                continue
            if length > 0.00001:
                direction = [x / length for x in tangent[:3]]
            else:
                normal = struct.unpack_from("<3f", data, normal_start + vertex * normal_stride)
                axis = min(range(3), key=lambda i: abs(normal[i]))
                direction = [float(i == axis) - normal[axis] * normal[i] for i in range(3)]
                magnitude = math.sqrt(sum(x * x for x in direction))
                direction = [x / magnitude for x in direction]
            struct.pack_into("<4f", data, start + vertex * stride, *direction, -1.0 if tangent[3] < 0 else 1.0)
            changed += 1
        if changed:
            changes.append({"accessor": index, "vectors": changed})

destination = root / "Sources/BistroRepaired/Bistro.glb"
destination.parent.mkdir(exist_ok=True)
destination.write_bytes(data)
for name in ("source.json", "ATTRIBUTION.txt"):
    if source.with_name(name).exists():
        shutil.copyfile(source.with_name(name), destination.with_name(name))
report = {"source": "../Bistro/Bistro.glb", "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
          "sha256": hashlib.sha256(data).hexdigest(), "changes": changes,
          "repairedVectors": sum(x["vectors"] for x in changes)}
destination.with_name("repair.json").write_text(json.dumps(report, indent=2))
print(f"Repaired {report['repairedVectors']} tangent vectors in {len(changes)} accessors: {destination}")
