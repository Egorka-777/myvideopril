"""Finite labelled dialogue contract: synthetic sessions and async mocks, no network."""
import copy
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest import IsolatedAsyncioTestCase
from unittest.mock import AsyncMock, patch

from test_worker import w, errors, functions, types, session_bytes
from telethon.crypto import AuthKey
from telethon.sessions import MemorySession, StringSession

ORIGINAL_OPEN_ACCOUNT = w.open_account


def account(identity):
    session = MemorySession()
    session.set_dc(2, '149.154.167.51', 443)
    session.auth_key = AuthKey(session_bytes())
    return dict(id=identity, session=StringSession.save(session), api_id=1234,
                api_hash='a' * 32, proxy='')


class DialogueClient:
    def __init__(self, existing=True):
        self.target = types.Channel(id=99, title='Gina fixture', photo=types.ChatPhotoEmpty(),
                                    date=None, megagroup=True, broadcast=False)
        self.existing = existing
        self.requests = []
        self.messages = []
        self.fail = None
        self.import_error = None
        self.pending = False
        self.message_base = 100
        self.disconnect = AsyncMock()

    async def __call__(self, request):
        self.requests.append(request)
        if isinstance(request, functions.messages.CheckChatInviteRequest):
            if self.existing:
                return types.ChatInviteAlready(chat=self.target)
            return types.ChatInvite(title='Gina fixture', photo=types.PhotoEmpty(id=0),
                                    participants_count=2, color=0, channel=True, megagroup=True,
                                    request_needed=self.pending)
        if isinstance(request, functions.messages.ImportChatInviteRequest):
            if self.import_error:
                if isinstance(self.import_error, errors.UserAlreadyParticipantError):
                    self.existing = True
                raise self.import_error
            self.existing = True
            return SimpleNamespace()
        raise AssertionError(type(request))

    async def get_input_entity(self, target):
        return types.InputPeerChannel(target.id, 123)

    async def send_message(self, target, text, **kwargs):
        self.messages.append((target, text, kwargs))
        if self.fail:
            raise self.fail
        return SimpleNamespace(id=self.message_base + len(self.messages), date=None)


class AiDialogue(IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        w.CANCEL.clear()
        self.events = []
        self.clients = {'a': DialogueClient(), 'b': DialogueClient()}
        self.clients['b'].message_base = 200
        self.job = dict(action='ai_dialogue', accounts=[account('a'), account('b')],
                        target='https://t.me/+eT4x_vqimZoxNDNh', delay_min=0, delay_max=0,
                        steps=[dict(account_id='a', text='Учебный пример', reply_to=0),
                               dict(account_id='b', text='Ответ ИИ', reply_to=1)])
        self.open = AsyncMock(side_effect=lambda acc, **kw: (self.clients[acc['id']], SimpleNamespace(id=42)))
        self.pause = AsyncMock()
        self.patches = [patch.object(w, 'open_account', self.open),
                        patch.object(w, 'pause', self.pause),
                        patch.object(w, 'emit', lambda event, **data: self.events.append((event, data)))]
        for item in self.patches:
            item.start()

    async def asyncTearDown(self):
        for item in reversed(self.patches):
            item.stop()
        w.CANCEL.clear()

    def event(self, name):
        return [data for kind, data in self.events if kind == name]

    async def test_prefix_plain_text_and_prior_step_reply_mapping(self):
        self.job['steps'][1]['text'] = '[bypass](https://example.org) **ИИ**'
        self.job['steps'].append(dict(account_id='a', text='Третий шаг', reply_to=2))
        await w.ai_dialogue(self.job)
        a, b = self.clients['a'], self.clients['b']
        self.assertEqual(a.messages[0][1], '[ИИ-диалог] Учебный пример')
        self.assertEqual(b.messages[0][1], '[ИИ-диалог] ' + self.job['steps'][1]['text'])
        self.assertIsNone(a.messages[0][2]['reply_to'])
        self.assertEqual(b.messages[0][2]['reply_to'], 101)
        self.assertEqual(a.messages[1][2]['reply_to'], 201)
        for client in (a, b):
            for _, text, options in client.messages:
                self.assertTrue(text.startswith('[ИИ-диалог] '))
                self.assertIsNone(options['parse_mode'])
                self.assertFalse(options['link_preview'])
            self.assertEqual(client.disconnect.await_count, 1 + len(client.messages))
        self.assertEqual([d['step'] for d in self.event('dialogue_sent')], [1, 2, 3])
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=3)])
        self.assertEqual(self.open.await_count, 5)
        self.assertTrue(all(call.kwargs == dict(single_attempt=True) for call in self.open.await_args_list))
        self.assertEqual(self.pause.await_count, 2)

    async def test_basic_group_reply_uses_receiving_accounts_local_id(self):
        client = SimpleNamespace(get_messages=AsyncMock(return_value=[
            SimpleNamespace(id=900, sender_id=42, date=None, message='[ИИ-диалог] hello')]))
        sent = dict(id=101, sender_id=42, date=None, text='[ИИ-диалог] hello')
        result = await w.dialogue_reply_id(client, types.InputPeerChat(99), sent)
        self.assertEqual(result, 900)
        client.get_messages.assert_awaited_once()
        client.get_messages.return_value = []
        with self.assertRaisesRegex(ValueError, 'однозначно'):
            await w.dialogue_reply_id(client, types.InputPeerChat(99), sent)

    async def test_existing_members_do_not_import(self):
        await w.ai_dialogue(self.job)
        self.assertEqual(len(self.event('dialogue_joined')), 2)
        for client in self.clients.values():
            self.assertEqual([type(r) for r in client.requests], [functions.messages.CheckChatInviteRequest])

    async def test_import_once_and_confirm_membership(self):
        self.clients['a'].existing = False
        await w.ai_dialogue(self.job)
        self.assertEqual([type(r) for r in self.clients['a'].requests],
                         [functions.messages.CheckChatInviteRequest,
                          functions.messages.ImportChatInviteRequest,
                          functions.messages.CheckChatInviteRequest])

    async def test_already_participant_race_uses_membership_check_not_retry(self):
        client = self.clients['a']
        client.existing = False
        client.import_error = errors.UserAlreadyParticipantError(request=None)
        await w.ai_dialogue(self.job)
        self.assertEqual(sum(isinstance(r, functions.messages.ImportChatInviteRequest) for r in client.requests), 1)
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=2)])

    async def test_all_validation_before_any_network(self):
        invalid = [
            dict(accounts=[]), dict(accounts=[self.job['accounts'][0]] * 2),
            dict(accounts=[dict(self.job['accounts'][0], id='')]),
            dict(accounts=[dict(self.job['accounts'][0], api_hash='invalid')]),
            dict(accounts=[dict(self.job['accounts'][0], session='invalid')]),
            dict(target='https://t.me/user'), dict(target=42), dict(steps=[]),
            dict(delay_min=-1), dict(delay_min=True), dict(delay_max=float('inf')),
            dict(delay_max=float('nan')), dict(delay_min=2, delay_max=1),
            dict(steps=[dict(account_id='missing', text='x', reply_to=0)]),
            dict(steps=[dict(account_id='a', text='', reply_to=0)]),
            dict(steps=[dict(account_id='a', text='x' * 4096, reply_to=0)]),
            dict(steps=[dict(account_id='a', text='\ud800', reply_to=0)]),
            dict(steps=[dict(account_id='a', text='x', reply_to=True)]),
            dict(steps=[dict(account_id='a', text='x', reply_to=1)]),
            dict(steps=[self.job['steps'][0], dict(account_id='b', text='late invalid', reply_to=2)]),
            dict(steps=[self.job['steps'][0], dict(account_id='b', text='late invalid', reply_to=-1)]),
        ]
        for changes in invalid:
            with self.subTest(changes=repr(changes)):
                job = copy.deepcopy(self.job)
                job.update(changes)
                with self.assertRaises((ValueError, TypeError)):
                    await w.ai_dialogue(job)
        self.open.assert_not_awaited()
        self.assertFalse(any(client.messages or client.requests for client in self.clients.values()))

    async def test_send_failure_reports_partial_and_stops_all_accounts_without_retry(self):
        self.clients['b'].fail = errors.FloodWaitError(request=None, capture=123)
        self.job['steps'].append(dict(account_id='a', text='Не отправлять', reply_to=0))
        with self.assertRaises(errors.FloodWaitError):
            await w.ai_dialogue(self.job)
        self.assertEqual([len(c.messages) for c in self.clients.values()], [1, 1])
        self.assertEqual(len(self.event('dialogue_sent')), 1)
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 1)
        self.assertEqual(self.event('dialogue_failed')[0]['stage'], 'send')
        self.assertTrue(any(d.get('status') == 'flood_wait' and d.get('seconds') == 123 for d in self.event('account')))
        self.assertFalse(self.event('dialogue_complete'))
        self.assertEqual(self.event('summary')[0]['failures'], 1)
        for client in self.clients.values():
            self.assertEqual(client.disconnect.await_count, 1 + len(client.messages))

    async def test_lost_send_response_is_never_retried(self):
        self.clients['a'].fail = TimeoutError()
        with self.assertRaises(TimeoutError):
            await w.ai_dialogue(self.job)
        self.assertEqual(len(self.clients['a'].messages), 1)
        self.assertFalse(self.clients['b'].messages)
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 0)
        self.assertFalse(self.event('dialogue_complete'))

    async def test_pending_approval_stops_before_any_send(self):
        for pending_preview in (True, False):
            with self.subTest(pending_preview=pending_preview):
                client = self.clients['b']
                client.existing = False
                client.pending = pending_preview
                client.import_error = errors.InviteRequestSentError(request=None)
                with self.assertRaisesRegex(ValueError, 'одобрени'):
                    await w.ai_dialogue(self.job)
                self.assertFalse(any(c.messages for c in self.clients.values()))
                self.assertFalse(self.event('dialogue_complete'))
                imports = sum(isinstance(r, functions.messages.ImportChatInviteRequest) for r in client.requests)
                self.assertEqual(imports, 0 if pending_preview else 1)

    async def test_user_and_broadcast_targets_rejected(self):
        for target in (types.User(id=42), types.Channel(id=99, title='Broadcast',
                       photo=types.ChatPhotoEmpty(), date=None, broadcast=True)):
            self.clients['a'].target = target
            with self.assertRaisesRegex(ValueError, 'групп'):
                await w.ai_dialogue(self.job)
            self.assertFalse(any(c.messages for c in self.clients.values()))

    async def test_nonmember_broadcast_is_rejected_before_import(self):
        client = self.clients['a']
        preview = types.ChatInvite(title='Broadcast fixture', photo=types.PhotoEmpty(id=0),
                                   participants_count=2, color=0, channel=True, broadcast=True)
        with patch.object(DialogueClient, '__call__', AsyncMock(return_value=preview)) as rpc:
            with self.assertRaisesRegex(ValueError, 'вещательн'):
                await w.ai_dialogue(self.job)
        self.assertEqual(rpc.await_count, 1)
        self.assertFalse(client.messages)

    async def test_successful_import_without_confirmed_membership_is_not_success(self):
        client = self.clients['a']
        preview = types.ChatInvite(title='Pending fixture', photo=types.PhotoEmpty(id=0),
                                   participants_count=2, color=0, channel=True, megagroup=True)
        with patch.object(DialogueClient, '__call__', AsyncMock(side_effect=[preview, SimpleNamespace(), preview])) as rpc:
            with self.assertRaisesRegex(ValueError, 'не подтверждено'):
                await w.ai_dialogue(self.job)
        self.assertEqual(rpc.await_count, 3)
        self.assertFalse(client.messages)
        self.assertFalse(self.event('dialogue_complete'))

    async def test_cancel_during_pause_stops_and_disconnects(self):
        self.pause.side_effect = lambda _: w.CANCEL.set()
        with self.assertRaises(w.StopJob):
            await w.ai_dialogue(self.job)
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 1)
        self.assertFalse(self.clients['b'].messages)
        self.assertFalse(self.event('dialogue_complete'))
        for client in self.clients.values():
            self.assertEqual(client.disconnect.await_count, 1 + len(client.messages))

    async def test_open_failure_disconnects_previously_opened_client(self):
        self.open.side_effect = [(self.clients['a'], SimpleNamespace(id=42)), TimeoutError()]
        with self.assertRaises(TimeoutError):
            await w.ai_dialogue(self.job)
        self.clients['a'].disconnect.assert_awaited_once()
        self.assertEqual(len(self.clients['a'].requests), 1)
        self.assertFalse(self.clients['a'].messages)
        self.assertEqual(self.event('dialogue_failed')[0]['stage'], 'connect')

    async def test_run_dispatch_and_complete_only_on_success(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['root'] = root
            await w.run(self.job)
        self.assertEqual(len(self.event('complete')), 1)
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=2)])

    async def test_single_attempt_open_does_not_try_other_connection_candidates(self):
        # The shared opener keeps its existing defaults; only dialogue opts out of fallbacks.
        with patch.object(w, 'account_route', AsyncMock()), \
             patch.object(w, 'connection_candidates', return_value=[dict(id='a'), dict(id='a')]), \
             patch.object(w, 'client_for', return_value=self.clients['a']) as make, \
             patch.object(w, 'connect', AsyncMock(side_effect=TimeoutError())), \
             patch.object(w, 'probe_proxy', AsyncMock()), \
             patch.object(w, 'open_account', ORIGINAL_OPEN_ACCOUNT):
            with self.assertRaises(w.SessionConnectionError):
                await w.open_account(dict(id='a'), single_attempt=True)
        self.assertEqual(make.call_count, 1)

    def attachment(self, root, name):
        path = Path(root) / name
        path.write_bytes(b'local fixture; never uploaded')
        return str(path)

    def file_sender(self, identity, *, fail=False):
        client = self.clients[identity]
        async def send(target, file, **options):
            if fail:
                raise TimeoutError('ambiguous file result')
            return SimpleNamespace(id=client.message_base + 50, date=None)
        client.send_file = AsyncMock(side_effect=send)
        return client.send_file

    async def test_media_caption_plaintext_metadata_and_reply(self):
        with tempfile.TemporaryDirectory() as root:
            path = self.attachment(root, 'photo.jpg')
            self.job['steps'][0]['media_path'] = path
            send = self.file_sender('a')
            await w.ai_dialogue(self.job)
        send.assert_awaited_once_with(types.InputPeerChannel(99, 123), str(Path(path).resolve()),
                                     caption='[ИИ-диалог] Учебный пример', reply_to=None, parse_mode=None)
        self.assertFalse(self.clients['a'].messages)
        self.assertEqual(self.clients['b'].messages[0][2]['reply_to'], 150)
        self.assertEqual([d['part'] for d in self.event('dialogue_sent')], ['media', 'text'])

    async def test_sticker_sends_label_then_sticker_and_main_reply_is_label(self):
        for extension in ('webp', 'tgs', 'webm'):
            with self.subTest(extension=extension), tempfile.TemporaryDirectory() as root:
                self.events.clear()
                for client in self.clients.values():
                    client.messages.clear()
                path = self.attachment(root, 'sticker.' + extension)
                self.job['steps'][0]['sticker_path'] = path
                send = self.file_sender('a')
                await w.ai_dialogue(self.job)
                self.assertEqual(send.await_args.kwargs['reply_to'], 101)
                self.assertIsNone(send.await_args.kwargs['parse_mode'])
                self.assertIsInstance(send.await_args.kwargs['attributes'][0], types.DocumentAttributeSticker)
                self.assertEqual(self.clients['b'].messages[0][2]['reply_to'], 101)
                self.assertEqual([d['part'] for d in self.event('dialogue_sent')], ['text', 'sticker', 'text'])
                self.assertEqual(self.event('dialogue_complete'), [dict(sent=3)])

    async def test_sticker_ambiguous_error_keeps_label_count_without_retry(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['steps'][0]['sticker_path'] = self.attachment(root, 's.webp')
            send = self.file_sender('a', fail=True)
            with self.assertRaises(TimeoutError):
                await w.ai_dialogue(self.job)
        send.assert_awaited_once()
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 1)
        self.assertEqual(self.event('dialogue_failed')[0]['total'], 3)
        self.assertEqual(len(self.event('dialogue_sent')), 1)
        self.assertFalse(self.clients['b'].messages)

    async def test_media_ambiguous_error_never_retries(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['steps'][0]['media_path'] = self.attachment(root, 'p.mp4')
            send = self.file_sender('a', fail=True)
            with self.assertRaises(TimeoutError):
                await w.ai_dialogue(self.job)
        send.assert_awaited_once()
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 0)

    async def test_invalid_controls_and_attachments_preflight_no_network(self):
        changes = [dict(limits=None), dict(schedule=None),
                   *[dict(limits={key: value}) for key in ('per_account_hour', 'per_account_run', 'per_target_run')
                     for value in (-1, True, 1.5, '2')],
                   *[dict(schedule=value) for value in (
                       dict(enabled=1), dict(enabled=True, hour_start=8, hour_end=8),
                       dict(hour_start=24), dict(hour_end=25), dict(hour_start=True),
                       dict(start_utc='2026-11-20T10:00:00'), dict(start_utc='bad'),
                       dict(start_utc='2026-11-20T10:00:00+03:00'))]]
        with tempfile.TemporaryDirectory() as root:
            photo = self.attachment(root, 'p.jpg')
            sticker = self.attachment(root, 's.webp')
            bad_steps = [dict(media_path=photo, sticker_path=sticker),
                         dict(media_path=photo, text='x' * 1024),
                         dict(media_path='https://example.com/p.jpg'),
                         dict(sticker_path='file:///s.webp'), dict(media_path='//server/p.jpg'),
                         dict(media_path=self.attachment(root, 'p.exe')), dict(sticker_path=photo),
                         dict(media_path=str(Path(root) / 'missing.jpg')), dict(media_path=42)]
            changes += [dict(steps=[dict(self.job['steps'][0], **value)]) for value in bad_steps]
            for change in changes:
                with self.subTest(change=change), self.assertRaises(ValueError):
                    await w.ai_dialogue(dict(self.job, **change))
        self.open.assert_not_awaited()

    async def test_total_limits_reject_whole_script_including_sticker(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['steps'][0]['sticker_path'] = self.attachment(root, 's.webp')
            for limits in (dict(per_account_run=1), dict(per_target_run=2)):
                with self.subTest(limits=limits), self.assertRaisesRegex(ValueError, 'превышает'):
                    await w.ai_dialogue(dict(self.job, limits=limits))
            self.open.assert_not_awaited()
            self.file_sender('a')
            await w.ai_dialogue(dict(self.job, limits=dict(per_account_run=2, per_target_run=3)))
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=3)])

    async def test_start_waits_before_first_connection_and_cancel_stops(self):
        self.job['schedule'] = dict(enabled=True, start_utc='2026-11-20T10:00:00Z')
        now = datetime(2026, 11, 20, 9, tzinfo=timezone.utc).timestamp()
        async def stop(seconds):
            self.open.assert_not_awaited()
            self.assertEqual(seconds, 3600)
            w.CANCEL.set()
        self.pause.side_effect = stop
        with patch.object(w.time, 'time', return_value=now), self.assertRaises(w.StopJob):
            await w.ai_dialogue(self.job)
        self.assertEqual(self.event('dialogue_waiting')[0]['reason'], 'start')
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 0)
        self.assertFalse(any(c.requests for c in self.clients.values()))

    async def test_local_daily_window_overnight_and_exclusive_end(self):
        # These timestamps are constructed in machine LOCAL time intentionally.
        for start, end, hour, opened in ((9, 17, 9, True), (9, 17, 17, False),
                                         (22, 6, 23, True), (22, 6, 3, True),
                                         (22, 6, 6, False), (0, 24, 23, True),
                                         (22, 0, 0, False), (22, 24, 23, True)):
            with self.subTest(start=start, end=end, hour=hour):
                gate = w.DialogueGate(*w.dialogue_controls(dict(schedule=dict(
                    enabled=True, hour_start=start, hour_end=end))))
                now = datetime(2026, 11, 20, hour).timestamp()
                with patch.object(w.time, 'time', return_value=now):
                    seconds, reason, text = gate.delay('a', False)
                self.assertEqual(seconds == 0, opened)
                if not opened:
                    self.assertEqual(datetime.fromtimestamp(now + seconds).hour, start)
                    self.assertEqual(reason, 'schedule')
                    self.assertTrue(text)

    async def test_scheduler_waits_rechecks_and_disabled_schedule_does_not_wait(self):
        gate = w.DialogueGate(*w.dialogue_controls(dict(schedule=dict(
            enabled=True, hour_start=9, hour_end=17))))
        clock = [datetime(2026, 11, 20, 8).timestamp()]
        self.pause.side_effect = lambda seconds: clock.__setitem__(0, clock[0] + seconds)
        with patch.object(w.time, 'time', side_effect=lambda: clock[0]):
            await gate.wait('a')
        self.pause.assert_awaited_once_with(3600)
        self.assertEqual(self.event('dialogue_waiting')[0]['reason'], 'schedule')
        disabled = w.DialogueGate(*w.dialogue_controls(dict(schedule=dict(
            enabled=False, start_utc='2099-01-01T00:00:00Z', hour_start=9, hour_end=9))))
        await disabled.wait('a')
        self.assertEqual(self.pause.await_count, 1)

    async def test_hourly_window_is_per_account_and_current_run_only(self):
        gate = w.DialogueGate(*w.dialogue_controls(dict(limits=dict(per_account_hour=2))))
        clock = [100.0]
        with patch.object(w, 'dialogue_clock', side_effect=lambda: clock[0]):
            gate.record('a')
            clock[0] = 110
            gate.record('a')
            self.assertEqual(gate.delay('b', True)[0], 0)
            self.assertEqual(gate.delay('a', False)[0], 0)  # JOIN does not consume messages.
            self.assertEqual(gate.delay('a', True)[0], 3590)
            self.pause.side_effect = lambda seconds: clock.__setitem__(0, clock[0] + seconds)
            await gate.wait('a', sending=True)
            self.assertEqual(clock[0], 3700)
            self.assertEqual(gate.sent['a'], [110])
            fresh = w.DialogueGate(*w.dialogue_controls(dict(limits=dict(per_account_hour=2))))
            self.assertEqual(fresh.delay('a', True)[0], 0)

    async def test_hourly_wait_between_sticker_parts_uses_disconnected_session(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['steps'][0]['sticker_path'] = self.attachment(root, 's.webp')
            self.job['limits'] = dict(per_account_hour=1)
            self.file_sender('a')
            clock = [100.0]
            async def advance(seconds):
                if seconds:
                    self.assertEqual(self.clients['a'].disconnect.await_count, 2)
                clock[0] += seconds
            self.pause.side_effect = advance
            with patch.object(w, 'dialogue_clock', side_effect=lambda: clock[0]):
                await w.ai_dialogue(self.job)
        self.assertEqual(self.event('dialogue_waiting')[0]['reason'], 'hour_limit')
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=3)])

    async def test_cancel_between_sticker_parts_reports_confirmed_label(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['steps'][0]['sticker_path'] = self.attachment(root, 's.webp')
            self.job['limits'] = dict(per_account_hour=1)
            send = self.file_sender('a')
            self.pause.side_effect = lambda seconds: w.CANCEL.set()
            with self.assertRaises(w.StopJob):
                await w.ai_dialogue(self.job)
        send.assert_not_awaited()
        self.assertEqual(self.event('dialogue_failed')[0]['sent'], 1)
        self.assertEqual(self.event('dialogue_sent')[0]['part'], 'text')

    async def test_window_closes_between_steps_waits_before_next_send(self):
        self.job['schedule'] = dict(enabled=True, hour_start=9, hour_end=17)
        clock = [datetime(2026, 11, 20, 16).timestamp()]
        async def advance(seconds):
            if seconds == 0:
                clock[0] = datetime(2026, 11, 20, 17).timestamp()
            else:
                self.assertEqual(self.open.await_count, 3)  # two JOINs and first send only
                clock[0] += seconds
        self.pause.side_effect = advance
        with patch.object(w.time, 'time', side_effect=lambda: clock[0]):
            await w.ai_dialogue(self.job)
        self.assertEqual(self.event('dialogue_waiting')[0]['reason'], 'schedule')
        self.assertEqual(self.event('dialogue_waiting')[0]['seconds'], 16 * 3600)
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=2)])

    async def test_window_closes_during_connection_disconnects_before_wait(self):
        self.job['schedule'] = dict(enabled=True, hour_start=9, hour_end=17)
        clock = [datetime(2026, 11, 20, 16).timestamp()]
        connections = [0]
        def connected(acc, **kwargs):
            connections[0] += 1
            if connections[0] == 3:  # first sender connects at the closing boundary
                clock[0] = datetime(2026, 11, 20, 17).timestamp()
            return self.clients[acc['id']], SimpleNamespace(id=42)
        self.open.side_effect = connected
        async def advance(seconds):
            if seconds:
                self.assertFalse(self.clients['a'].messages)
                self.assertEqual(self.clients['a'].disconnect.await_count, 2)
                clock[0] += seconds
        self.pause.side_effect = advance
        with patch.object(w.time, 'time', side_effect=lambda: clock[0]):
            await w.ai_dialogue(self.job)
        self.assertEqual(self.open.await_count, 5)
        self.assertEqual(len(self.clients['a'].messages), 1)
        self.assertEqual(self.event('dialogue_complete'), [dict(sent=2)])

    async def test_hourly_limit_resets_between_worker_jobs(self):
        self.job['limits'] = dict(per_account_hour=1)
        with patch.object(w, 'dialogue_clock', return_value=100):
            await w.ai_dialogue(self.job)
            await w.ai_dialogue(self.job)
        self.assertFalse(self.event('dialogue_waiting'))

    async def test_input_eof_cancels_active_dialogue_timer(self):
        with patch.object(w.sys, 'stdin', []):
            w.watch_input(cancel_on_eof=True)
        with self.assertRaises(w.StopJob):
            await w.ai_dialogue(self.job)
        self.open.assert_not_awaited()

    async def test_basic_group_sticker_and_media_use_label_text_date_for_lookup(self):
        with tempfile.TemporaryDirectory() as root:
            self.job['steps'][0]['sticker_path'] = self.attachment(root, 's.webp')
            send = self.file_sender('a')
            for client in self.clients.values():
                client.get_input_entity = AsyncMock(return_value=types.InputPeerChat(99))
                client.get_messages = AsyncMock(return_value=[SimpleNamespace(
                    id=777, sender_id=42, date=None, message='[ИИ-диалог] Учебный пример')])
            await w.ai_dialogue(self.job)
            self.assertEqual(send.await_args.kwargs['reply_to'], 777)
            self.assertEqual(self.clients['b'].messages[0][2]['reply_to'], 777)
            self.events.clear()
            self.job['steps'][0].pop('sticker_path')
            self.job['steps'][0]['media_path'] = self.attachment(root, 'p.jpg')
            await w.ai_dialogue(self.job)
            self.assertEqual(self.clients['b'].messages[-1][2]['reply_to'], 777)
