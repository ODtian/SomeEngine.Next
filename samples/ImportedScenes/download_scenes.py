"""Download original scene files, dependencies and attribution. No mesh conversion."""
import concurrent.futures
import hashlib
import json
import pathlib
import subprocess
import urllib.parse

root = pathlib.Path(__file__).parent / "Sources"

def download(url, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not destination.exists():
        temporary = destination.with_name(destination.name + ".download")
        try:
            subprocess.run(["curl.exe", "--fail", "--location", "--silent", "--show-error", "--retry", "3",
                            "--output", str(temporary), url], check=True)
            temporary.replace(destination)
        finally:
            temporary.unlink(missing_ok=True)
    data = destination.read_bytes()
    return {"path": destination.name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}

base = "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/Sponza/"
sponza = root / "Sponza"
source = download(base + "glTF/Sponza.gltf", sponza / "Sponza.gltf")
model = json.loads((sponza / "Sponza.gltf").read_bytes())
dependencies = sorted({item["uri"] for key in ("buffers", "images") for item in model.get(key, [])
                       if "uri" in item and not item["uri"].startswith("data:")})
def dependency(uri):
    path = pathlib.PurePosixPath(urllib.parse.unquote(uri))
    if path.is_absolute() or ".." in path.parts:
        raise ValueError(f"Unexpected dependency path: {uri}")
    return download(base + "glTF/" + uri, sponza / path)
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    files = list(pool.map(dependency, dependencies))
for name in ("README.md", "LICENSE.md"):
    download(base + name, sponza / name)
download("https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/LICENSES/LicenseRef-CRYENGINE-Agreement.txt",
         sponza / "Cryengine-Agreement.txt")
(sponza / "source.json").write_text(json.dumps({"url": base + "glTF/Sponza.gltf", **source, "dependencies": files}, indent=2))

bistro_url = "https://media.githubusercontent.com/media/PacktPublishing/The-Modern-Vulkan-Cookbook/main/source/common/resources/assets/Bistro.glb"
bistro = download(bistro_url, root / "Bistro/Bistro.glb")
if bistro["sha256"] != "5cc23d52b95fa1ba8b585d998185904becf8bcaa8f424fd74cf6950addb63deb":
    raise ValueError("Bistro GLB differs from the tested upstream Git LFS object.")
(root / "Bistro/source.json").write_text(json.dumps({"url": bistro_url, **bistro}, indent=2))
(root / "Bistro/ATTRIBUTION.txt").write_text(
    "Amazon Lumberyard Bistro, Open Research Content Archive (ORCA), Amazon Lumberyard, July 2017.\n"
    "https://developer.nvidia.com/orca/amazon-lumberyard-bistro\n"
    "CC BY 4.0: https://creativecommons.org/licenses/by/4.0/\n"
    "GLB conversion distributed with PacktPublishing/The-Modern-Vulkan-Cookbook.\n")
print(f"Sponza: {len(files)} dependencies. Bistro: {bistro['bytes']} bytes. Source hashes saved.")
