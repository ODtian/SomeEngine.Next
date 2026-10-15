"""Build the author renderer and measure the shared transparency inputs on Windows."""

import argparse
from datetime import datetime, timezone
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from urllib.request import urlopen
from zipfile import ZipFile


SOURCE_REVISION = "807fc6ddaf5c3a8eb07c0e174a16344fbd04df03"


def run(arguments, directory, **options):
    subprocess.run([str(value) for value in arguments], cwd=directory, check=True, **options)


def download_zip(url):
    with urlopen(url) as response:
        return ZipFile(io.BytesIO(response.read()))


def combine_results(checkout, output):
    configurations = [json.loads((checkout / f"lucid-df{count}-final.json").read_text()) for count in (3, 8, 16)]
    result = {
        "CapturedAtUtc": datetime.now(timezone.utc).isoformat(),
        "Source": "https://github.com/nadult/lucid",
        "SourceRevision": SOURCE_REVISION,
        "License": "GNU GPL v3, inherited by the reference adapter and patch",
        "Adapter": "References/LucidComparison.cpp and References/LucidVisibility.patch",
        "Timing": "GPU timestamps; full pipeline and cached geometry measured separately; common image clear precedes each timed scope",
        "Protocol": "At least 250 ms of GPU warmup, then 32 warmup frames and 24 samples per method; all GPU applications run sequentially",
        "Quality": "128 x 128 RGB against complete CPU sorted compositing; performance at 1280 x 720",
        "Memory": "Explicit renderer buffers and source vertex/index buffers; geometry and bin references sized from this input; author scratch layout and worker count retained",
        "Material": "Identical RGBA8 flat triangle colors, with barycentric sample coverage and zero-alpha reduction; RGBA16F output",
        "Configurations": configurations,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


def main():
    arguments = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parents[3]
    arguments.add_argument("--output", type=Path, default=root / "wiki/architecture/rendering/Transparency-Lucid-Reference-2026-10-15.json")
    options = arguments.parse_args()
    output = options.output.resolve()
    cmake = shutil.which("cmake") or r"C:\Program Files\CMake\bin\cmake.exe"
    with tempfile.TemporaryDirectory(prefix="someengine-lucid-") as directory:
        checkout = Path(directory) / "lucid"
        run(["git", "clone", "https://github.com/nadult/lucid", checkout], root)
        run(["git", "checkout", SOURCE_REVISION], checkout)
        run(["git", "submodule", "update", "--init", "--recursive"], checkout)
        cache = next(value for value in json.loads((checkout / "libfwk/dependencies.json").read_text())["package-caches"] if value["platform"] == "windows")
        dependencies = checkout / "dependencies"
        dependencies.mkdir()
        for package in cache["packages"]:
            name, version, _ = package.split(":")
            with download_zip(cache["url"] + f"{name}_{version}.zip") as archive:
                archive.extractall(dependencies)

        # Supply the vector routines used by the author's current shaderc package.
        with download_zip("https://github.com/microsoft/STL/archive/refs/tags/vs-2022-17.14.zip") as archive:
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                relative = entry.filename.split("/", 1)[1]
                if relative.startswith("stl/inc/"):
                    destination = checkout / "stl-inc" / relative.removeprefix("stl/inc/")
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(archive.read(entry))
                elif relative == "stl/src/vector_algorithms.cpp":
                    source = archive.read(entry).replace(b"_Traits::_Shuffle_avx<", b"_Traits::template _Shuffle_avx<")
                    source = source.replace(b"_Traits::_Spread_avx<", b"_Traits::template _Spread_avx<")
                    (checkout / "vector_algorithms.cpp").write_bytes(source)

        references = Path(__file__).resolve().parent
        run(["git", "apply", "--ignore-space-change", references / "LucidVisibility.patch"], checkout)
        run([sys.executable, "libfwk/tools/configure.py", "-T", "Release", "-G", "ninja-clang-cl",
             "-DFWK_UNITY_BUILD=ON", f"-DTRANSPARENCY_SOURCE={(references / 'LucidComparison.cpp').as_posix()}"], checkout)
        run([cmake, "--build", "build", "-j", "8"], checkout)
        run(["dotnet", "run", "--project", root / "tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj",
             "-c", "Release", "--", "--export-lucid", checkout / "input.bin"], root)
        for count in (3, 8, 16):
            environment = os.environ.copy()
            environment["LUCID_DEPTH_FILTER"] = str(count)
            run([checkout / "lucid.exe", "--transparency-comparison", "input.bin", f"lucid-df{count}-final.json"], checkout, env=environment)
        combine_results(checkout, output)
    print(output)


if __name__ == "__main__":
    main()
