import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('android_launcher', ROOT / 'tools/android/worker.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class LauncherTests(unittest.TestCase):
    def setUp(self):
        self.bound = m.binding('891387432')
        self.args = ['io.nekohasekai.sfa\n', '1\n', '19: tun0: <POINTOPOINT,UP,LOWER_UP>',
                     'android.title=String (VideoBatch-891387432)',
                     'ESTAB 0 0 10.0.2.16:40000 192.0.2.1:1080', self.bound, ('192.0.2.1', 1080)]

    def test_confirmed_binding(self):
        self.assertEqual('box10d15f6a', self.bound['avd'])

    def test_other_profile_has_no_binding(self):
        with self.assertRaisesRegex(m.OpenError, 'unbound'):
            m.binding('999999999')

    def test_unbound_never_touches_native_tools(self):
        with patch.object(m.subprocess, 'run') as run:
            with self.assertRaisesRegex(m.OpenError, 'unbound'):
                m.open_profile(dict(action='open', profile_id='999999999', package='com.zhiliaoapp.musically'))
            run.assert_not_called()

    def test_matching_active_route(self):
        self.assertTrue(m.route_matches(*self.args))

    def test_reject_changed_proxy(self):
        self.args[-1] = ('192.0.2.2', 1080)
        self.assertFalse(m.route_matches(*self.args))

    def test_reject_disabled_lockdown(self):
        self.args[1] = '0'
        self.assertFalse(m.route_matches(*self.args))

    def test_reject_other_vpn(self):
        self.args[0] = 'other.vpn'
        self.assertFalse(m.route_matches(*self.args))

    def test_reject_missing_tunnel(self):
        self.args[2] = 'Device tun0 does not exist'
        self.assertFalse(m.route_matches(*self.args))

    def test_reject_other_profile_notification(self):
        self.args[3] = 'android.title=String (VideoBatch-839421592)'
        self.assertFalse(m.route_matches(*self.args))

    def test_reject_direct_egress(self):
        self.args[4] += '\nESTAB 0 0 10.0.2.16:40001 198.51.100.1:443'
        self.assertFalse(m.route_matches(*self.args))

    def test_reject_no_proxy_connection(self):
        self.args[4] = 'ESTAB 0 0 172.19.0.1:40000 198.51.100.1:443'
        self.assertFalse(m.route_matches(*self.args))

    def test_proxy_credentials_not_required_in_commands(self):
        self.assertEqual(('192.0.2.1', 1080), m.proxy_endpoint('socks5://user:secret@192.0.2.1:1080'))

    def test_unsupported_scheme_fails_closed(self):
        with self.assertRaisesRegex(m.OpenError, 'vpn_unverified'):
            m.proxy_endpoint('http://192.0.2.1:1080')

    def test_existing_device_open_checks_route_before_and_after(self):
        job = dict(action='open', profile_id='891387432', package='com.zhiliaoapp.musically',
                   proxy='socks5://u:p@192.0.2.1:1080', sdk='sdk', avd_home='avds')
        def reply(*args, **kwargs):
            if args[:2] == ('shell', 'getprop'):
                return '1'
            if args[:3] == ('shell', 'cmd', 'package'):
                return 'com.zhiliaoapp.musically/.Main'
            if args[:3] == ('shell', 'am', 'start'):
                return 'Status: ok'
            if args[:2] == ('shell', 'dumpsys'):
                return 'topResumedActivity=com.zhiliaoapp.musically/.Main'
            return ''
        from types import SimpleNamespace
        with patch.object(m.Path, 'exists', return_value=True), \
             patch.object(m.subprocess, 'run', return_value=SimpleNamespace(stdout='emulator-5554 device')), \
             patch.object(m.subprocess, 'Popen') as launch, \
             patch.object(m.Device, 'run', side_effect=reply), \
             patch.object(m.Device, 'verify_device'), \
             patch.object(m.Device, 'verify_route') as route, \
             patch.object(m, 'emit') as emit:
            m.open_profile(job)
            launch.assert_not_called()
            self.assertEqual(2, route.call_count)
            self.assertEqual('ready', emit.call_args.args[0])

    def test_vpn_failure_never_opens_app(self):
        job = dict(action='open', profile_id='891387432', package='com.zhiliaoapp.musically',
                   proxy='socks5://u:p@192.0.2.1:1080', sdk='sdk', avd_home='avds')
        from types import SimpleNamespace
        with patch.object(m.Path, 'exists', return_value=True), \
             patch.object(m.subprocess, 'run', return_value=SimpleNamespace(stdout='emulator-5554 device')), \
             patch.object(m.Device, 'run', return_value='1') as run, \
             patch.object(m.Device, 'verify_device'), \
             patch.object(m.Device, 'verify_route', side_effect=m.OpenError('vpn_unverified')), \
             patch.object(m.time, 'monotonic', side_effect=[0, 0, 0, 46]), \
             patch.object(m, 'emit'):
            with self.assertRaisesRegex(m.OpenError, 'vpn_unverified'):
                m.open_profile(job)
            self.assertFalse(any(c.args[:3] == ('shell', 'am', 'start') for c in run.call_args_list))

    def test_invalid_warmup_parameters_never_touch_device(self):
        for duration, percent in [(0, 30), (-1, 30), (60, -1), (60, 101), (True, 30), (60, '30')]:
            with patch.object(m, 'binding') as binding:
                with self.assertRaisesRegex(m.OpenError, 'invalid_request'):
                    m.open_profile(dict(action='warmup', package=m.PACKAGE, duration_seconds=duration, like_percent=percent))
                binding.assert_not_called()

    def test_only_tiktok_warmup_is_supported(self):
        with patch.object(m, 'binding') as binding:
            with self.assertRaisesRegex(m.OpenError, 'invalid_request'):
                m.open_profile(dict(action='warmup', package='com.instagram.android', duration_seconds=60, like_percent=30))
            binding.assert_not_called()

    def test_cancelled_device_never_sends_another_command(self):
        import threading
        stopped = threading.Event()
        stopped.set()
        device = m.Device('sdk', self.bound, stopped)
        with patch.object(m.subprocess, 'run') as run:
            with self.assertRaisesRegex(m.OpenError, 'cancelled'):
                device.run('shell', 'input', 'tap', '100', '100')
            run.assert_not_called()

    def test_worker_exits_with_control_pipe_still_open(self):
        import subprocess
        import sys
        process = subprocess.Popen([sys.executable, str(ROOT / 'tools/android/worker.py')],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            process.stdin.write(b'{"action":"invalid"}\n')
            process.stdin.flush()
            self.assertEqual(1, process.wait(timeout=10))
            import json
            self.assertEqual('error', json.loads(process.stdout.read())['event'])
            self.assertEqual(b'', process.stderr.read())
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            process.stdin.close()
            process.stdout.close()
            process.stderr.close()

    def test_wrong_device_refused(self):
        device = m.Device('sdk', self.bound)
        with patch.object(device, 'run', return_value='other-avd\nOK\n'):
            with self.assertRaisesRegex(m.OpenError, 'wrong_device'):
                device.verify_device()


if __name__ == '__main__':
    unittest.main()
