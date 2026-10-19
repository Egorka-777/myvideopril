"""Owner profile edits: fake RPC only, no Telegram sessions or remote connections."""
import hashlib
import tempfile
from pathlib import Path
from types import SimpleNamespace
from unittest import IsolatedAsyncioTestCase
from unittest.mock import patch
from test_worker import w, functions, errors


class ProfileClient:
    def __init__(self):
        self.first='Old';self.last='Name';self.photo=17;self.requests=[];self.uploads=[];self.disconnected=False
        self.fail=None;self.wrong_readback=False
    async def get_me(self):
        return SimpleNamespace(id=42,first_name='WRONG' if self.wrong_readback else self.first,last_name=self.last,photo=SimpleNamespace(photo_id=self.photo))
    async def upload_file(self,data,**kwargs):self.uploads.append((data,kwargs));return 'uploaded-fixture'
    async def __call__(self,request):
        self.requests.append(request)
        if isinstance(request,functions.account.UpdateProfileRequest):
            assert request.about is None,'bio must be preserved'
            if self.fail=='names':raise errors.FloodWaitError(request=request,capture=30)
            self.first=request.first_name;self.last=request.last_name;return await self.get_me()
        if isinstance(request,functions.photos.UploadProfilePhotoRequest):
            if self.fail=='photo':raise TimeoutError('synthetic lost response')
            self.photo=99;return SimpleNamespace(photo=SimpleNamespace(id=99))
        raise AssertionError(type(request))
    async def disconnect(self):self.disconnected=True


class ProfileTemplate(IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.client=ProfileClient();self.events=[];w.CANCEL.clear()
        self.account=dict(id='a'*64,user_id=42,proxy='socks5://fixture:secret@127.0.0.1:1080')
        self.job=dict(root=str(self.root),accounts=[self.account],profile=dict(first_name='Иван',last_name='Петров',avatar='',avatar_sha256=''))
    async def asyncTearDown(self):self.tmp.cleanup();w.CANCEL.clear()
    def image(self):
        data=b'\xff\xd8synthetic-jpeg-fixture\xff\xd9';digest=hashlib.sha256(data).hexdigest();folder=self.root/'profile-avatars';folder.mkdir(exist_ok=True);path=folder/(digest+'.jpg');path.write_bytes(data)
        self.job['profile'].update(avatar=str(path),avatar_sha256=digest);return data,digest
    async def apply(self):
        async def connect(account):
            self.assertIs(account,self.account);return self.client,await self.client.get_me()
        with patch.object(w,'open_account',connect),patch.object(w,'emit',lambda event,**data:self.events.append((event,data))):
            await w.apply_profile_template(self.job)
    async def test_name_photo_verified_and_source_preserved(self):
        data,digest=self.image();await self.apply()
        self.assertEqual(len(self.client.uploads),1);self.assertEqual(self.client.uploads[0][0],data)
        self.assertEqual([type(r) for r in self.client.requests],[functions.account.UpdateProfileRequest,functions.photos.UploadProfilePhotoRequest])
        event=next(data for kind,data in self.events if kind=='profile_updated');self.assertEqual(event['avatar_sha256'],digest);self.assertEqual(event['photo_id'],'99');self.assertEqual(event['first_name'],'Иван')
        self.assertEqual(Path(self.job['profile']['avatar']).read_bytes(),data);self.assertTrue(self.client.disconnected)
    async def test_no_avatar_preserves_photo_and_empty_surname_is_transmitted(self):
        import struct
        self.job['profile']['last_name']='';await self.apply()
        request=self.client.requests[0];self.assertEqual(request.last_name,'');self.assertTrue(struct.unpack('<i',bytes(request)[4:8])[0]&2)
        self.assertEqual(self.client.photo,17);self.assertEqual(self.client.uploads,[])
    async def test_reapplying_confirmed_photo_does_not_upload_again(self):
        _,digest=self.image();self.account.update(profile_avatar_hash=digest,profile_photo_id='17');await self.apply();self.assertEqual(self.client.uploads,[])
        self.client.photo=18;await self.apply();self.assertEqual(len(self.client.uploads),1)
    async def test_partial_photo_failure_never_reports_success_or_retries(self):
        self.image();self.client.fail='photo'
        with self.assertRaisesRegex(w.ProfileChangeError,'частично'):await self.apply()
        self.assertEqual(self.client.first,'Иван');self.assertEqual(len(self.client.uploads),1);self.assertEqual(sum(isinstance(r,functions.photos.UploadProfilePhotoRequest) for r in self.client.requests),1)
        self.assertFalse(any(kind=='profile_updated' for kind,_ in self.events));self.assertTrue(self.client.disconnected)
    async def test_wrong_identity_rejected_before_writes(self):
        self.account['user_id']=43
        with self.assertRaisesRegex(w.ProfileChangeError,'другой аккаунт'):await self.apply()
        self.assertEqual(self.client.requests,[]);self.assertEqual(self.client.uploads,[])
    async def test_invalid_template_or_asset_has_no_network(self):
        with patch.object(w,'open_account',side_effect=AssertionError('network')):
            self.job['accounts']=[self.account,self.account]
            with self.assertRaisesRegex(ValueError,'один аккаунт'):await w.apply_profile_template(self.job)
            self.job['accounts']=[self.account];self.image();Path(self.job['profile']['avatar']).write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError,'изменилось'):await w.apply_profile_template(self.job)
            self.job['profile'].update(avatar='',avatar_sha256='',first_name='')
            with self.assertRaises(ValueError):await w.apply_profile_template(self.job)
    async def test_readback_mismatch_is_not_success(self):
        count=0
        original=self.client.get_me
        async def current():
            nonlocal count
            count+=1;self.client.wrong_readback=count>1;return await original()
        self.client.get_me=current
        async def no_delay(_):pass
        with patch.object(w,'pause',no_delay):
            with self.assertRaisesRegex(w.ProfileChangeError,'не подтвердил'):await self.apply()
        self.assertFalse(any(kind=='profile_updated' for kind,_ in self.events));self.assertEqual(len(self.client.requests),1)
    async def test_flood_wait_is_saved_and_stops_before_photo(self):
        self.image();self.client.fail='names'
        with self.assertRaises(w.ProfileChangeError):await self.apply()
        self.assertEqual(self.client.uploads,[]);self.assertEqual(len(self.client.requests),1)
        self.assertTrue(any(kind=='account' and data['status']=='flood_wait' and data['seconds']==30 for kind,data in self.events))
    async def test_cancel_before_writes_keeps_profile(self):
        w.CANCEL.set()
        with self.assertRaises(w.StopJob):await self.apply()
        self.assertEqual(self.client.requests,[]);self.assertTrue(self.client.disconnected)
