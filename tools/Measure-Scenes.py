"""Run DefaultGame's scene measurements on the queue's selected computer."""

import argparse
import contextlib
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application")
    parser.add_argument("--application-argument", action="append", default=[])
    parser.add_argument("--managed", action="store_true")
    parser.add_argument("--trace", action="store_true")
    parser.add_argument("--trace-detail", action="store_true")
    parser.add_argument("--shading-path", choices=["deferred", "forward"])
    parser.add_argument("--scene-playback", choices=["static", "motion", "materials", "camera"], default="camera")
    parser.add_argument("--scene")
    parser.add_argument("--expected-scene")
    parser.add_argument("--scene-configuration-json")
    parser.add_argument("--startup-frames", type=int)
    parser.add_argument("--on-default-desktop", action="store_true", help=argparse.SUPPRESS)
    result = parser.parse_args()
    if result.trace_detail and not result.trace:
        parser.error("--trace-detail requires --trace")
    if result.scene and result.expected_scene and result.scene != result.expected_scene:
        parser.error("--scene and --expected-scene must select the same scene")
    reserved = {"--measure", "--measure-trace", "--measure-scene", "--test-scene", "--shading-path",
                "--scene-playback", "--frames", "--trace-detail"}
    for value in result.application_argument:
        if value.split("=", 1)[0] in reserved:
            parser.error("use the measurement parameter for " + value)
    return result


def run_on_default_desktop():
    from ctypes import wintypes
    import msvcrt

    class StartInfo(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("reserved", wintypes.LPWSTR),
                    ("desktop", wintypes.LPWSTR), ("title", wintypes.LPWSTR),
                    ("x", wintypes.DWORD), ("y", wintypes.DWORD),
                    ("width", wintypes.DWORD), ("height", wintypes.DWORD),
                    ("columns", wintypes.DWORD), ("rows", wintypes.DWORD),
                    ("fill", wintypes.DWORD), ("flags", wintypes.DWORD),
                    ("show", wintypes.WORD), ("reserved_size", wintypes.WORD),
                    ("reserved_bytes", ctypes.POINTER(ctypes.c_byte)),
                    ("input", wintypes.HANDLE), ("output", wintypes.HANDLE), ("error", wintypes.HANDLE)]

    class ChildInfo(ctypes.Structure):
        _fields_ = [("process", wintypes.HANDLE), ("thread", wintypes.HANDLE),
                    ("process_id", wintypes.DWORD), ("thread_id", wintypes.DWORD)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.GetStdHandle.argtypes = [wintypes.DWORD]
    kernel.GetStdHandle.restype = wintypes.HANDLE
    kernel.DuplicateHandle.argtypes = [wintypes.HANDLE, wintypes.HANDLE, wintypes.HANDLE,
                                      ctypes.POINTER(wintypes.HANDLE), wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.CreateProcessW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p,
                                     ctypes.c_void_p, wintypes.BOOL, wintypes.DWORD,
                                     ctypes.c_void_p, wintypes.LPCWSTR,
                                     ctypes.POINTER(StartInfo), ctypes.POINTER(ChildInfo)]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handles = []

    def inherit(handle):
        copy = wintypes.HANDLE()
        current = kernel.GetCurrentProcess()
        if not kernel.DuplicateHandle(current, handle, current, ctypes.byref(copy), 0, True, 2):
            raise ctypes.WinError(ctypes.get_last_error())
        handles.append(copy)
        return copy

    child = ChildInfo()
    try:
        with open(os.devnull, "rb") as input_file:
            start = StartInfo(cb=ctypes.sizeof(StartInfo), desktop=r"WinSta0\Default", flags=0x100,
                              input=inherit(msvcrt.get_osfhandle(input_file.fileno())),
                              output=inherit(kernel.GetStdHandle(-11)), error=inherit(kernel.GetStdHandle(-12)))
            command = ctypes.create_unicode_buffer(subprocess.list2cmdline(
                [sys.executable, "-B", str(Path(__file__).resolve()), *sys.argv[1:], "--on-default-desktop"]))
            if not kernel.CreateProcessW(sys.executable, command, None, None, True, 0x08000000,
                                         None, os.getcwd(), ctypes.byref(start), ctypes.byref(child)):
                raise ctypes.WinError(ctypes.get_last_error())
            wait = kernel.WaitForSingleObject(child.process, 60000)
            if wait == 0x102:
                subprocess.run(["taskkill", "/PID", str(child.process_id), "/T", "/F"], capture_output=True)
                raise TimeoutError("The desktop measurement exceeded 60 seconds.")
            if wait != 0:
                raise ctypes.WinError(ctypes.get_last_error())
            code = wintypes.DWORD()
            if not kernel.GetExitCodeProcess(child.process, ctypes.byref(code)):
                raise ctypes.WinError(ctypes.get_last_error())
            return code.value
    finally:
        for handle in [child.thread, child.process, *handles]:
            if handle:
                kernel.CloseHandle(handle)


def foreground_window(process_id):
    from ctypes import wintypes

    user = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.ShowWindowAsync.argtypes = [wintypes.HWND, ctypes.c_int]
    user.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                  ctypes.c_int, ctypes.c_int, wintypes.UINT]
    found = False

    @callback_type
    def select(window, _):
        nonlocal found
        pid = wintypes.DWORD()
        user.GetWindowThreadProcessId(window, ctypes.byref(pid))
        if pid.value == process_id:
            user.ShowWindowAsync(window, 5)
            user.SetWindowPos(window, wintypes.HWND(-1), 0, 0, 0, 0, 0x4043)
            found = True
            return False
        return True

    user.EnumWindows(select, 0)
    return found


def application_path(options):
    if options.application:
        return Path(options.application).resolve()
    project = Path(__file__).resolve().parents[1] / "samples/DefaultGame/DefaultGame.csproj"
    evaluated = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-p:Configuration=Release",
                                "-p:PublishAot=" + ("false" if options.managed else "true"),
                                "-getProperty:TargetDir,PublishDir,AssemblyName,UseAppHost"],
                               capture_output=True, text=True, check=True)
    properties = json.loads(evaluated.stdout)["Properties"]
    if properties["UseAppHost"] != "true":
        raise RuntimeError("Build DefaultGame with an application host before measuring.")
    directory = Path(properties["TargetDir" if options.managed else "PublishDir"])
    if not directory.is_absolute():
        directory = project.parent / directory
    return directory / (properties["AssemblyName"] + (".exe" if os.name == "nt" else ""))


def run_application(application, values, environment):
    print("NATIVE_LAUNCH " + json.dumps({"Executable": str(application), "Arguments": values}), flush=True)
    started = time.monotonic()
    process = subprocess.Popen([str(application), *values], cwd=application.parent,
                               env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    foreground = os.name != "nt" or not any(value in {"--measure", "--measure-trace"} for value in values)
    output = errors = ""
    try:
        while True:
            if not foreground:
                foreground = foreground_window(process.pid)
            try:
                output, errors = process.communicate(timeout=min(.1, max(.001, 59 - (time.monotonic() - started))))
                break
            except subprocess.TimeoutExpired:
                if time.monotonic() - started >= 59:
                    raise TimeoutError("The scene measurement exceeded 60 seconds including application startup.")
    finally:
        if process.poll() is None:
            process.kill()
            output, errors = process.communicate()
        sys.stdout.write(output)
        sys.stderr.write(errors)
    if process.returncode:
        raise RuntimeError("DefaultGame exited with code " + str(process.returncode))
    return time.monotonic() - started


def check_result(report, samples, options, configuration):
    if report["NativeAot"] is not (not options.managed):
        raise RuntimeError("The report compilation mode must match the requested NativeAOT or managed run.")
    if report["TraceEnabled"] is not options.trace:
        raise RuntimeError("The report trace mode must match the measurement.")
    if options.shading_path and report["ShadingPath"] != options.shading_path.capitalize():
        raise RuntimeError("The report shading path must match the measurement.")
    expected = [options.expected_scene or options.scene] if options.expected_scene or options.scene else \
        ["1024 spheres", "Sponza", "Amazon Bistro"]
    scenes = report["Scenes"]
    if report["Complete"] is not True or sorted(scene["Scene"] for scene in scenes) != sorted(expected) or \
            any(scene["Status"] != "Measured" for scene in scenes):
        raise RuntimeError("The requested scene measurement must complete every selected scene.")
    configured = {scene["Name"]: scene for scene in configuration}
    windows = {scene["Scene"]: scene for scene in samples["Scenes"]}
    for scene in scenes:
        window = configured[scene["Scene"]].get("MeasurementFrames")
        if window:
            first, count = window["FirstFrame"], window["FrameCount"]
            frames = windows[scene["Scene"]]["Frames"]
            if scene["Frames"] != count or scene["RequestedSimulationWindow"] != window or \
                    scene["FirstSimulationFrame"] != first or scene["LastSimulationFrame"] != first + count - 1 or \
                    [frame["SimulationFrame"] for frame in frames] != list(range(first, first + count)):
                raise RuntimeError("The scene samples must cover the configured simulation-frame interval.")
    return len(expected) == 3 and not options.managed and not options.trace and all(
        configured[name].get("MeasurementFrames") == {"FirstFrame": 2048, "FrameCount": 1024} for name in expected)


def main():
    options = arguments()
    if os.name == "nt" and not options.on_default_desktop:
        return run_on_default_desktop()
    application = application_path(options)
    values = ["--measure-scene", options.scene] if options.scene else []
    values += options.application_argument
    if sys.platform == "linux" and not any(value.split("=", 1)[0] in
                                           {"--graphics-backend", "--backend"} for value in values):
        values += ["--graphics-backend", "vulkan"]
    if options.shading_path:
        values += ["--shading-path", options.shading_path]
    if options.scene_playback != "static":
        values += ["--scene-playback", options.scene_playback]
    if options.startup_frames:
        values += ["--frames", str(options.startup_frames), "--no-debug-ui"]
    else:
        values += ["--measure-trace" if options.trace else "--measure"]
    if options.trace_detail:
        values += ["--trace-detail"]
    with contextlib.ExitStack() as cleanup:
        scene_configuration = application.parent / "test-scenes.json"
        if options.scene_configuration_json:
            previous_configuration = scene_configuration.read_bytes()
            cleanup.callback(scene_configuration.write_bytes, previous_configuration)
            scene_configuration.write_text(options.scene_configuration_json, encoding="utf-8")
        if options.trace:
            diagnostics = application.parent / "Performance" / time.strftime("%Y%m%d-%H%M%S")
            diagnostics.mkdir(parents=True)
            temporary = str(diagnostics)
            print("SCENE_DIAGNOSTICS " + json.dumps({"Directory": temporary}), flush=True)
        else:
            temporary = cleanup.enter_context(tempfile.TemporaryDirectory(prefix="someengine-scene-measurement-"))
        if sys.platform == "linux":
            from x11_display import open_display
            environment = cleanup.enter_context(open_display(1280, 720))
            # Older loaders filter newer instance extensions provided by the driver.
            environment.setdefault("VK_LOADER_DISABLE_INST_EXT_FILTER", "1")
        else:
            environment = os.environ.copy()
        environment["SOMEENGINE_MEASUREMENT_OUTPUT_DIRECTORY"] = temporary
        environment["SOMEENGINE_MEASUREMENT_OUTPUT_BASENAME"] = "measurement"
        if options.trace:
            environment["SOMEENGINE_STARTUP_TRACE"] = str(Path(temporary) / "startup.setrace")
        else:
            environment.pop("SOMEENGINE_STARTUP_TRACE", None)
        elapsed = run_application(application, values, environment)
        if options.startup_frames:
            print("Startup completed: requested frames " + str(options.startup_frames) + "; exit 0.")
            return 0
        report = json.loads((Path(temporary) / "measurement-results.json").read_text(encoding="utf-8-sig"))
        samples = json.loads((Path(temporary) / report["FrameSamples"]).read_text(encoding="utf-8-sig"))
        configuration = json.loads(scene_configuration.read_text(encoding="utf-8-sig"))
        print("SCENE_REPORT " + json.dumps(report, separators=(",", ":")))
        print("SCENE_FRAME_SAMPLES " + json.dumps(samples, separators=(",", ":")))
        print("Measurement elapsed: " + str(round(elapsed, 2)) + " seconds including application startup.")
        comparison = check_result(report, samples, options, configuration)
        for scene in report["Scenes"]:
            print(f'{scene["Scene"]}: {scene["Status"]}; {scene["Frames"]} frames; '
                  f'median {scene["MedianMs"]:.4f} ms; P95 {scene["P95Ms"]:.4f} ms')
        if comparison:
            print("Three-scene P95 below 1 ms: " + str(all(scene["P95Ms"] < 1 for scene in report["Scenes"])))
        else:
            print("Diagnostic scene measurement.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
