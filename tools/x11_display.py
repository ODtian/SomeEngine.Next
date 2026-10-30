"""Keep an X11 display alive for one queued graphics command."""

import contextlib
import ctypes
import os
from pathlib import Path
import select
import subprocess
import tempfile
import uuid


class ApplicationInfo(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint), ("next", ctypes.c_void_p),
        ("name", ctypes.c_char_p), ("version", ctypes.c_uint),
        ("engine", ctypes.c_char_p), ("engine_version", ctypes.c_uint),
        ("api_version", ctypes.c_uint),
    ]


class InstanceInfo(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint), ("next", ctypes.c_void_p),
        ("flags", ctypes.c_uint), ("app", ctypes.POINTER(ApplicationInfo)),
        ("layers", ctypes.c_uint), ("layer_names", ctypes.c_void_p),
        ("extensions", ctypes.c_uint), ("extension_names", ctypes.c_void_p),
    ]


class PciInfo(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint), ("next", ctypes.c_void_p),
        ("domain", ctypes.c_uint), ("bus", ctypes.c_uint),
        ("device", ctypes.c_uint), ("function", ctypes.c_uint),
    ]


class DeviceIdentity(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint), ("next", ctypes.c_void_p),
        ("uuid", ctypes.c_ubyte * 16), ("driver_uuid", ctypes.c_ubyte * 16),
        ("luid", ctypes.c_ubyte * 8), ("node", ctypes.c_uint),
        ("luid_valid", ctypes.c_uint),
    ]


class DeviceProperties(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint), ("next", ctypes.c_void_p),
        ("data", ctypes.c_ubyte * 2048),
    ]


def read_gpu_address():
    """Read the single Vulkan device selected by the shared queue."""
    api = ctypes.CDLL("libvulkan.so.1")
    api.vkCreateInstance.argtypes = [ctypes.POINTER(InstanceInfo), ctypes.c_void_p,
                                   ctypes.POINTER(ctypes.c_void_p)]
    api.vkEnumeratePhysicalDevices.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint),
                                             ctypes.POINTER(ctypes.c_void_p)]
    api.vkGetPhysicalDeviceProperties2.argtypes = [ctypes.c_void_p, ctypes.POINTER(DeviceProperties)]
    api.vkGetPhysicalDeviceProperties2.restype = None
    api.vkDestroyInstance.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    api.vkDestroyInstance.restype = None
    application = ApplicationInfo(type=0, api_version=(1 << 22) | (3 << 12))
    description = InstanceInfo(type=1, app=ctypes.pointer(application))
    instance = ctypes.c_void_p()
    result = api.vkCreateInstance(ctypes.byref(description), None, ctypes.byref(instance))
    if result:
        raise RuntimeError(f"vkCreateInstance returned {result}")
    try:
        count = ctypes.c_uint()
        api.vkEnumeratePhysicalDevices(instance, ctypes.byref(count), None)
        devices = (ctypes.c_void_p * count.value)()
        api.vkEnumeratePhysicalDevices(instance, ctypes.byref(count), devices)
        pci = PciInfo(type=1000212000)
        identity = DeviceIdentity(type=1000071004, next=ctypes.addressof(pci))
        properties = DeviceProperties(type=1000059001, next=ctypes.addressof(identity))
        api.vkGetPhysicalDeviceProperties2(devices[0], ctypes.byref(properties))
        gpu_id = "GPU-" + str(uuid.UUID(bytes=bytes(identity.uuid)))
        bus_id = f"PCI:{pci.bus}:{pci.device}:{pci.function}"
        return gpu_id, bus_id
    finally:
        api.vkDestroyInstance(instance, None)


@contextlib.contextmanager
def open_display(width, height):
    """Yield child-process environment and release a task's private Xorg afterwards."""
    environment = os.environ.copy()
    if environment.get("DISPLAY"):
        yield environment
        return

    gpu_id, bus_id = read_gpu_address()
    with tempfile.TemporaryDirectory(prefix="SomeEngine-X11-") as temporary:
        directory = Path(temporary)
        configuration = directory / "xorg.conf"
        configuration.write_text(f"""Section "ServerFlags"
    Option "AutoAddGPU" "false"
    Option "AutoAddDevices" "false"
EndSection
Section "Files"
    ModulePath "/usr/lib/x86_64-linux-gnu/nvidia/xorg"
    ModulePath "/usr/lib/xorg/modules"
EndSection
Section "Device"
    Identifier "QueueGpu"
    Driver "nvidia"
    BusID "{bus_id}"
    Option "AllowEmptyInitialConfiguration" "true"
    Option "UseDisplayDevice" "None"
EndSection
Section "Screen"
    Identifier "QueueScreen"
    Device "QueueGpu"
    DefaultDepth 24
    SubSection "Display"
        Depth 24
        Virtual {width} {height}
    EndSubSection
EndSection
""", encoding="utf-8")
        read_pipe, write_pipe = os.pipe()
        with (directory / "stdout.log").open("w+") as output:
            server = subprocess.Popen([
                "/usr/lib/xorg/Xorg", "-config", str(configuration),
                "-logfile", str(directory / "Xorg.log"), "-displayfd", str(write_pipe),
                "-nolisten", "tcp", "-noreset", "-seat", "someengine-benchmark",
            ], pass_fds=(write_pipe,), stdout=output, stderr=subprocess.STDOUT)
            os.close(write_pipe)
            try:
                try:
                    ready, _, _ = select.select([read_pipe], [], [], 10)
                    number = os.read(read_pipe, 32).strip().decode("ascii") if ready else ""
                finally:
                    os.close(read_pipe)
                if not number:
                    output.seek(0)
                    raise RuntimeError("The queued Xorg display failed to start:\n" + output.read())
                environment["DISPLAY"] = ":" + number
                print(f"X11 display={environment['DISPLAY']}; GPU={gpu_id}; {bus_id}", flush=True)
                yield environment
            finally:
                if server.poll() is None:
                    server.terminate()
                    try:
                        server.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        server.kill()
                        server.wait()
