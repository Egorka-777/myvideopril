import importlib.util
from pathlib import Path
import sys
import itertools
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

TOOLS = Path(__file__).resolve().parents[2] / 'tools/android'
sys.path.insert(0, str(TOOLS))
import feed_warmup as m


def ui(*items):
    root = ET.Element('hierarchy')
    for item in items:
        attributes = dict(package=m.PACKAGE, bounds='[0,0][720,1280]')
        attributes.update(item if isinstance(item, dict) else {'text': item})
        ET.SubElement(root, 'node', attributes)
    return root


def card(caption='first', liked=False, live=False):
    return ui('Home', 'For You', caption,
              {'class': 'android.widget.Button', 'content-desc': 'Video liked' if liked else 'Like video. 100 likes'},
              'Tap to watch LIVE' if live else '')


class FeedTests(unittest.TestCase):
    def setUp(self):
        self.device = Mock()
        self.stopped = Mock()
        self.stopped.wait.return_value = False
        self.emit = Mock()
        self.verify = Mock()
        self.classify = Mock(return_value=('verified', 'own_profile', '@account'))

    def run_feed(self, screens, seconds=1, probability=1, clock=None):
        ticks = clock or [0, 0, 0, 2, 2, 2]
        with patch.object(m, 'screen', side_effect=screens), patch.object(m.time, 'monotonic', side_effect=itertools.chain(ticks, itertools.repeat(ticks[-1]))):
            m.run(self.device, seconds, probability, self.stopped, self.emit, self.classify, self.verify)

    def test_stale_dump_is_never_read(self):
        def reply(*args):
            if args[:3] == ('shell', 'dumpsys', 'window'):
                return 'mCurrentFocus=Window{ x ' + m.PACKAGE + '/.Main}'
            if args[:3] == ('shell', 'uiautomator', 'dump'):
                return 'ERROR: could not get idle state'
            return ''
        self.device.run.side_effect = reply
        with self.assertRaisesRegex(m.WarmupError, 'Свежий'):
            m.screen(self.device)
        calls = [c.args for c in self.device.run.call_args_list]
        self.assertIn(('shell', 'rm', '-f', '/sdcard/vb-feed-warmup.xml'), calls)
        self.assertFalse(any(c[:2] == ('shell', 'cat') for c in calls))

    def test_login_or_manual_screen_refused(self):
        for label in ('Log in to TikTok', 'Security verification', "TikTok isn't responding"):
            root = ui(label)
            self.device.run.side_effect = [
                'mCurrentFocus=Window{ ' + m.PACKAGE + '/.Main}', '',
                'UI hierchary dumped to: /sdcard/vb-feed-warmup.xml', ET.tostring(root, encoding='unicode'),
                'mCurrentFocus=Window{ ' + m.PACKAGE + '/.Main}']
            with self.assertRaises(m.WarmupError):
                m.screen(self.device)

    def test_known_own_profile_node_confirms_saved_session(self):
        unknown = Mock(return_value=('unknown', 'no_identity_evidence', ''))
        root = ui('Edit profile', {'resource-id': m.PACKAGE + ':id/mje', 'text': 'saved.account'})
        self.assertTrue(m.authenticated(root, unknown))
        self.assertFalse(m.authenticated(ui('Edit profile'), unknown))

    def test_navigation_uses_clickable_parent_not_duplicate_text(self):
        root=ui({'text':'Home','clickable':'true'}, {'text':'Home','clickable':'false'})
        with patch.object(m,'screen',return_value=card()):
            m.navigate(self.device,root,'Home',self.stopped)
        self.assertEqual(1,len(self.device.run.call_args_list))

    def test_notification_permission_is_denied_without_grant(self):
        root=ui({'text':'Allow TikTok to send you notifications?','package':'com.google.android.permissioncontroller'},
                {'text':'Don’t allow','package':'com.google.android.permissioncontroller','resource-id':'com.google.android.permissioncontroller:id/permission_deny_button'})
        self.device.run.side_effect=[
            'mCurrentFocus=com.google.android.permissioncontroller/.GrantPermissionsActivity','',
            'UI hierchary dumped to: /sdcard/vb-feed-warmup.xml',ET.tostring(root,encoding='unicode'),'',
            'mCurrentFocus='+m.PACKAGE+'/.Main','',
            'UI hierchary dumped to: /sdcard/vb-feed-warmup.xml',ET.tostring(card(),encoding='unicode'),
            'mCurrentFocus='+m.PACKAGE+'/.Main']
        m.screen(self.device)
        self.assertEqual(1,len([c for c in self.device.run.call_args_list if c.args[:3]==('shell','input','tap')]))

    def test_existing_like_is_not_a_like_button(self):
        root = card(liked=True)
        self.assertTrue(m.liked(root))
        self.assertIsNone(m.like_button(root))

    def test_duration_finishes_without_late_like_or_swipe(self):
        self.run_feed([ui('Home'), card(), card()], clock=[0, 0, 0, 2, 2, 2])
        commands = [c.args for c in self.device.run.call_args_list]
        self.assertEqual(1, len(commands))  # known Home navigation only
        self.assertEqual('completed', self.emit.call_args.args[0])
        self.verify.assert_called()

    def test_missing_session_stops_before_feed(self):
        self.classify.return_value = ('unknown', 'no_identity_evidence', '')
        with self.assertRaisesRegex(m.WarmupError, 'авторизованная'):
            self.run_feed([ui('Profile'), ui('Home')])
        self.assertFalse(any(c.args[0] == 'completed' for c in self.emit.call_args_list))

    def test_like_is_confirmed_once(self):
        self.run_feed([ui('Home'), card(), card(), card(liked=True)], seconds=10,
                      clock=[0, 0, 0, 1, 1, 11, 11])
        taps = [c for c in self.device.run.call_args_list if c.args[:3] == ('shell', 'input', 'tap')]
        self.assertEqual(2, len(taps))  # Home then Like
        self.assertEqual(1, self.emit.call_args.kwargs['confirmed_likes'])

    def test_already_liked_video_is_never_tapped(self):
        self.run_feed([ui('Home'), card(liked=True), card(liked=True)], seconds=10,
                      clock=[0, 0, 0, 1, 1, 11, 11])
        self.assertEqual(1, len(self.device.run.call_args_list))
        self.assertEqual(0, self.emit.call_args.kwargs['confirmed_likes'])

    def test_missing_like_confirmation_fails_without_retry(self):
        with self.assertRaisesRegex(m.WarmupError, 'Лайк не подтверждён'):
            self.run_feed([ui('Home'), card(), card(), card()], seconds=10,
                          clock=[0, 0, 0, 1, 1, 1])
        self.assertFalse(any(c.args[0] == 'completed' for c in self.emit.call_args_list))
        self.assertEqual(2, len(self.device.run.call_args_list))

    def test_zero_probability_never_likes(self):
        self.run_feed([ui('Home'), card(), card()], seconds=10, probability=0,
                      clock=[0, 0, 0, 1, 1, 11, 11])
        self.assertEqual(1, len(self.device.run.call_args_list))
        self.assertEqual(0, self.emit.call_args.kwargs['confirmed_likes'])

    def test_live_is_skipped_without_like(self):
        self.run_feed([ui('Home'), card(live=True), card(live=True)],
                      clock=[0, 0, 0, 2, 2])
        self.assertEqual(1, len(self.device.run.call_args_list))
        self.assertEqual(1, self.emit.call_args.kwargs['live_skipped'])

    def test_unchanged_feed_after_swipe_is_not_success(self):
        with self.assertRaisesRegex(m.WarmupError, 'Лента не изменилась'):
            self.run_feed([ui('Home'), card(liked=True), card(liked=True), card(liked=True)], seconds=100,
                          clock=[0, 0, 0, 1, 1, 1, 1, 1, 20])
        self.assertFalse(any(c.args[0] == 'completed' for c in self.emit.call_args_list))

    def test_route_failure_is_not_success(self):
        self.verify.side_effect = RuntimeError('vpn_unverified')
        with self.assertRaisesRegex(RuntimeError, 'vpn_unverified'):
            self.run_feed([ui('Home'), card()])
        self.assertFalse(any(c.args[0] == 'completed' for c in self.emit.call_args_list))

    def test_cancellation_is_not_success(self):
        self.device.check_cancelled.side_effect = RuntimeError('cancelled')
        with self.assertRaisesRegex(RuntimeError, 'cancelled'):
            self.run_feed([ui('Home'), card()])
        self.assertFalse(any(c.args[0] == 'completed' for c in self.emit.call_args_list))


if __name__ == '__main__':
    unittest.main()
