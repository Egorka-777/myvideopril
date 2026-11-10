"""Select the local Whisper device without hiding CUDA initialization errors."""
import os
from pathlib import Path
import sys
import sysconfig

# Windows removes DLL search directories when their handles are collected.
_DLL_HANDLES = []
_DLL_DIRECTORIES = set()


def _add_cuda_dll_directories():
    if os.name != "nt":
        return
    nvidia = Path(sysconfig.get_path("purelib")) / "nvidia"
    for library in ("cublas", "cudnn"):
        directory = nvidia / library / "bin"
        if directory.is_dir() and str(directory) not in _DLL_DIRECTORIES:
            _DLL_HANDLES.append(os.add_dll_directory(str(directory)))
            _DLL_DIRECTORIES.add(str(directory))
            # CTranslate2 also uses LoadLibrary, which searches PATH.
            os.environ["PATH"] = str(directory) + os.pathsep + os.environ.get("PATH", "")


def _cuda_device_count(ctranslate2):
    if os.name == "nt":
        # CTranslate2 reports zero even for driver initialization errors.
        import ctypes
        try:
            driver = ctypes.WinDLL("nvcuda.dll")
        except OSError as error:
            if error.winerror == 126:  # NVIDIA driver is not installed.
                return 0
            raise
        status = driver.cuInit(0)
        if status == 100:  # CUDA_ERROR_NO_DEVICE
            return 0
        if status != 0:
            raise RuntimeError(f"NVIDIA CUDA driver initialization failed (cuInit={status})")
        count = ctypes.c_int()
        status = driver.cuDeviceGetCount(ctypes.byref(count))
        if status != 0:
            raise RuntimeError(f"NVIDIA CUDA device detection failed (cuDeviceGetCount={status})")
        if count.value == 0:
            return 0
        if ctranslate2.get_cuda_device_count() == 0:
            raise RuntimeError("NVIDIA device exists but CTranslate2 cannot initialize CUDA")
        return count.value
    return ctranslate2.get_cuda_device_count()


def create_model(model_name, root):
    """Return WhisperModel; root is the highlights data root containing models/."""
    import ctranslate2

    device, compute_type = "cpu", "int8"
    if _cuda_device_count(ctranslate2) > 0:
        _add_cuda_dll_directories()
        device = "cuda"
        supported = ctranslate2.get_supported_compute_types(device)
        compute_type = next((kind for kind in ("int8_float16", "float16", "float32")
                             if kind in supported), None)
        if compute_type is None:
            raise RuntimeError(f"CUDA device has no supported Whisper compute type: {sorted(supported)}")
    from faster_whisper import WhisperModel

    print(f"Whisper runtime: device={device}, compute_type={compute_type}, model={model_name}",
          file=sys.stderr, flush=True)
    return WhisperModel(model_name, device=device, compute_type=compute_type,
                        cpu_threads=max(1, (os.cpu_count() or 4) // 2),
                        download_root=str(Path(root) / "models"), local_files_only=True)
