"""Approved preset contract: fake RPC only; no account, auth or remote mutation."""
import hashlib
import tempfile
from pathlib import Path
from types import SimpleNamespace as NS
from unittest import IsolatedAsyncioTestCase
from unittest.mock import patch
from test_worker import w, functions, types, errors


class PresetClient:
    def __init__(self):
        self.first='Old';self.last='Surname';self.about='Old bio';self.photo=17
        self.requests=[];self.uploads=[];self.disconnected=False;self.fail=None;self.premium=False
    async def get_me(self):
        return NS(id=42,first_name=self.first,last_name=self.last,username='unchanged',premium=self.premium,photo=NS(photo_id=self.photo))
    async def upload_file(self,data,**kwargs):self.uploads.append(data);return 'upload'
    async def download_profile_photo(self,user,**kwargs):
        assert user.photo.photo_id==self.photo
        assert kwargs==dict(file=bytes,download_big=True)
        return None if self.fail=='download' else b'\xff\xd8server-recompressed-image\xff\xd9'
    async def __call__(self,request):
        self.requests.append(request)
        if isinstance(request,functions.help.GetAppConfigRequest):
            return NS(config=types.JsonObject([types.JsonObjectValue('about_length_limit_default',types.JsonNumber(70)),types.JsonObjectValue('about_length_limit_premium',types.JsonNumber(140))]))
        if isinstance(request,functions.account.UpdateProfileRequest):
            if self.fail=='flood':raise errors.FloodWaitError(request=request,capture=30)
            self.first=request.first_name;self.last=request.last_name;self.about=request.about
            if self.fail=='names':self.first='Wrong'
            return await self.get_me()
        if isinstance(request,functions.photos.UploadProfilePhotoRequest):
            if self.fail=='photo':raise TimeoutError('synthetic lost response')
            self.photo=99;return NS(photo=NS(id=99))
        if isinstance(request,functions.users.GetFullUserRequest):
            return NS(full_user=NS(id=42,about='Wrong' if self.fail=='bio' else self.about))
        raise AssertionError(type(request))
    async def disconnect(self):self.disconnected=True


class ProfilePreset(IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.events=[];self.client=PresetClient();w.CANCEL.clear()
        self.image=b'\xff\xd8fixture-image\xff\xd9';self.digest=hashlib.sha256(self.image).hexdigest()
        folder=self.root/'profile-avatars';folder.mkdir();self.path=folder/(self.digest+'.jpg');self.path.write_bytes(self.image)
        self.account=dict(id='a'*64,user_id=42,proxy='socks5://fixture:secret@127.0.0.1:1080')
        self.job=dict(action='profile_preset',root=str(self.root),accounts=[self.account],profile=dict(first_name='Иван Петров',last_name='',about='Новая биография',avatar=str(self.path),avatar_sha256=self.digest))
    async def asyncTearDown(self):self.tmp.cleanup();w.CANCEL.clear()
    async def apply(self):
        async def connect(account):
            self.assertIs(account,self.account);return self.client,await self.client.get_me()
        async def no_delay(_):pass
        with patch.object(w,'open_account',connect),patch.object(w,'emit',lambda kind,**data:self.events.append((kind,data))),patch.object(w,'pause',no_delay):
            await w.apply_profile_template(self.job)
    def writes(self):return [r for r in self.client.requests if isinstance(r,(functions.account.UpdateProfileRequest,functions.photos.UploadProfilePhotoRequest))]
    def success(self):return [data for kind,data in self.events if kind=='profile_preset_updated']
    async def test_complete_readback_includes_bio_and_downloaded_photo(self):
        await self.apply();request=self.writes()[0]
        self.assertEqual((request.first_name,request.last_name,request.about),('Иван Петров','','Новая биография'))
        self.assertEqual(self.client.uploads,[self.image]);self.assertEqual(self.path.read_bytes(),self.image)
        self.assertEqual(len(self.success()),1);self.assertEqual(self.success()[0]['photo_id'],'99')
        self.assertEqual(self.success()[0]['avatar_sha256'],self.digest)
        self.assertNotEqual(self.success()[0]['downloaded_avatar_sha256'],self.digest)
        self.assertTrue(self.client.disconnected)
    async def test_name_and_bio_not_truncated_or_trimmed(self):
        self.job['profile'].update(first_name=' '+('😀'*62)+' ',about='я'*70)
        await self.apply();self.assertEqual(self.writes()[0].first_name,self.job['profile']['first_name']);self.assertEqual(self.writes()[0].about,'я'*70)
    async def test_invalid_name_before_connection(self):
        for name in ('','x'*65,'Name\nSurname'):
            self.job['profile']['first_name']=name
            with patch.object(w,'open_account',side_effect=AssertionError('network')):
                with self.assertRaises(ValueError):await w.apply_profile_template(self.job)
    async def test_invalid_bio_or_surname_or_avatar_before_connection(self):
        for values in (dict(about=None),dict(about='a\nb'),dict(last_name='Surname'),dict(avatar='')):
            original=self.job['profile'].copy();self.job['profile'].update(values)
            with patch.object(w,'open_account',side_effect=AssertionError('network')):
                with self.assertRaises(ValueError):await w.apply_profile_template(self.job)
            self.job['profile']=original
    async def test_bio_limit_checked_before_all_writes(self):
        self.job['profile']['about']='я'*71
        with self.assertRaisesRegex(w.ProfileChangeError,'до 70'):await self.apply()
        self.assertEqual(self.writes(),[]);self.assertEqual(self.client.uploads,[]);self.assertFalse(self.success())
    async def test_premium_uses_server_premium_limit(self):
        self.client.premium=True;self.job['profile']['about']='a'*140;await self.apply();self.assertEqual(len(self.success()),1)
        self.job['profile']['about']='a'*141;before=len(self.writes())
        with self.assertRaisesRegex(w.ProfileChangeError,'до 140'):await self.apply()
        self.assertEqual(len(self.writes()),before)
    async def test_partial_mutation_or_readback_failure_never_succeeds(self):
        for failure in ('photo','bio','download','names'):
            self.client=PresetClient();self.client.fail=failure;self.events=[]
            with self.assertRaisesRegex(w.ProfileChangeError,'частично'):await self.apply()
            self.assertFalse(self.success());self.assertTrue(self.client.disconnected)
            self.assertEqual(sum(isinstance(r,functions.photos.UploadProfilePhotoRequest) for r in self.client.requests),1)
    async def test_wrong_identity_has_no_writes(self):
        self.account['user_id']=43
        with self.assertRaisesRegex(w.ProfileChangeError,'другой аккаунт'):await self.apply()
        self.assertEqual(self.client.requests,[])
    async def test_cancel_before_mutation(self):
        w.CANCEL.set()
        with self.assertRaises(w.StopJob):await self.apply()
        self.assertEqual(self.client.requests,[]);self.assertFalse(self.success())
    async def test_empty_bio_explicitly_clears_bio(self):
        self.job['profile']['about']='';await self.apply();self.assertEqual(self.writes()[0].about,'')
    async def test_flood_wait_no_photo_or_retry(self):
        self.client.fail='flood'
        with self.assertRaises(w.ProfileChangeError):await self.apply()
        self.assertEqual(len(self.writes()),1);self.assertEqual(self.client.uploads,[]);self.assertFalse(self.success())
        self.assertTrue(any(kind=='account' and data['status']=='flood_wait' for kind,data in self.events))
    async def test_already_confirmed_photo_is_downloaded_without_reupload(self):
        self.account.update(profile_avatar_hash=self.digest,profile_photo_id='17')
        await self.apply();self.assertEqual(self.client.uploads,[])
        self.assertEqual(self.success()[0]['photo_id'],'17')
        self.assertTrue(self.success()[0]['downloaded_avatar_sha256'])
    async def test_missing_server_limit_fails_before_writes(self):
        original=self.client.__class__.__call__
        async def missing(client,request):
            if isinstance(request,functions.help.GetAppConfigRequest):return NS(config=types.JsonObject([]))
            return await original(client,request)
        with patch.object(PresetClient,'__call__',missing):
            with self.assertRaisesRegex(w.ProfileChangeError,'не сообщил лимит'):await self.apply()
        self.assertEqual(self.writes(),[]);self.assertFalse(self.success())
    async def test_dispatch_preset(self):
        with patch.object(w,'apply_profile_template') as apply,patch.object(w,'emit'):
            await w.run(self.job);apply.assert_awaited_once_with(self.job)
