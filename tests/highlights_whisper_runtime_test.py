import importlib.util
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location(
    "whisper_runtime", Path(__file__).resolve().parents[1] / "tools/highlights/whisper_runtime.py")
runtime = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runtime)


class WhisperRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.ct2 = types.SimpleNamespace(get_cuda_device_count=Mock(return_value=1),
                                        get_supported_compute_types=Mock(return_value={"int8_float16", "float16"}))
        self.model = Mock()
        self.modules = patch.dict(sys.modules, {"ctranslate2": self.ct2,
                                               "faster_whisper": types.SimpleNamespace(WhisperModel=self.model)})
        self.modules.start()
        self.addCleanup(self.modules.stop)
        detection = patch.object(runtime, "_cuda_device_count", side_effect=lambda ct2: ct2.get_cuda_device_count())
        detection.start()
        self.addCleanup(detection.stop)

    def test_cuda_uses_quantized_half_precision_and_local_model(self):
        with patch.object(runtime, "_add_cuda_dll_directories") as dlls:
            self.assertIs(runtime.create_model("small", "data"), self.model.return_value)
        dlls.assert_called_once()
        self.assertEqual(self.model.call_args.kwargs["device"], "cuda")
        self.assertEqual(self.model.call_args.kwargs["compute_type"], "int8_float16")
        self.assertEqual(self.model.call_args.kwargs["download_root"], str(Path("data") / "models"))
        self.assertTrue(self.model.call_args.kwargs["local_files_only"])

    def test_no_cuda_device_uses_original_cpu_int8(self):
        self.ct2.get_cuda_device_count.return_value = 0
        with patch.object(runtime, "_add_cuda_dll_directories") as dlls:
            runtime.create_model("small", "data")
        dlls.assert_not_called()
        self.assertEqual(self.model.call_args.kwargs["device"], "cpu")
        self.assertEqual(self.model.call_args.kwargs["compute_type"], "int8")

    def test_cuda_model_failure_is_not_retried_on_cpu(self):
        self.model.side_effect = RuntimeError("missing cudnn64_9.dll")
        with patch.object(runtime, "_add_cuda_dll_directories"), self.assertRaisesRegex(RuntimeError, "cudnn64_9"):
            runtime.create_model("small", "data")
        self.model.assert_called_once()

    def test_cuda_detection_failure_is_not_hidden(self):
        self.ct2.get_cuda_device_count.side_effect = RuntimeError("broken driver")
        with self.assertRaisesRegex(RuntimeError, "broken driver"):
            runtime.create_model("small", "data")
        self.model.assert_not_called()

    def test_cuda_float32_device_still_uses_gpu(self):
        self.ct2.get_supported_compute_types.return_value = {"float32"}
        with patch.object(runtime, "_add_cuda_dll_directories"):
            runtime.create_model("small", "data")
        self.assertEqual(self.model.call_args.kwargs["compute_type"], "float32")
        self.assertEqual(self.model.call_args.kwargs["device"], "cuda")

    def test_dll_handles_retained_and_directories_registered_once(self):
        old_path = runtime.os.environ.get("PATH", "")
        self.addCleanup(runtime.os.environ.__setitem__, "PATH", old_path)
        with patch.object(runtime.os, "name", "nt"), \
             patch.object(runtime.sysconfig, "get_path", return_value="site-packages"), \
             patch.object(runtime.Path, "is_dir", return_value=True), \
             patch.object(runtime.os, "add_dll_directory", create=True) as add, \
             patch.object(runtime, "_DLL_HANDLES", []), \
             patch.object(runtime, "_DLL_DIRECTORIES", set()):
            runtime._add_cuda_dll_directories()
            runtime._add_cuda_dll_directories()
            self.assertEqual(add.call_count, 2)
            self.assertEqual(len(runtime._DLL_HANDLES), 2)
            self.assertIn("cudnn", runtime.os.environ["PATH"])


class CudaDetectionTests(unittest.TestCase):
    def test_driver_initialization_error_is_explicit(self):
        driver = Mock()
        driver.cuInit.return_value = 35
        with patch.object(runtime.os, "name", "nt"), \
             patch("ctypes.WinDLL", return_value=driver), \
             self.assertRaisesRegex(RuntimeError, "cuInit=35"):
            runtime._cuda_device_count(Mock())

    def test_driver_no_device_is_cpu(self):
        driver = Mock()
        driver.cuInit.return_value = 100
        with patch.object(runtime.os, "name", "nt"), patch("ctypes.WinDLL", return_value=driver):
            self.assertEqual(runtime._cuda_device_count(Mock()), 0)

    def test_ctranslate_failure_with_present_device_is_explicit(self):
        driver = Mock()
        driver.cuInit.return_value = 0
        def get_count(pointer):
            pointer._obj.value = 1
            return 0
        driver.cuDeviceGetCount.side_effect = get_count
        ct2 = Mock()
        ct2.get_cuda_device_count.return_value = 0
        with patch.object(runtime.os, "name", "nt"), \
             patch("ctypes.WinDLL", return_value=driver), \
             self.assertRaisesRegex(RuntimeError, "cannot initialize CUDA"):
            runtime._cuda_device_count(ct2)


if __name__ == "__main__":
    unittest.main()
