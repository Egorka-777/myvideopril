from pathlib import Path
from playwright.sync_api import sync_playwright
r=Path(__file__).resolve().parent;html=(r/'ClipFlow.html').read_text()
with sync_playwright() as pw:
 b=pw.chromium.launch(executable_path='/usr/bin/chromium',args=['--no-sandbox']);pg=b.new_page(viewport={'width':1600,'height':1000});errors=[];pg.on('pageerror',lambda e: errors.append(str(e)));pg.set_content(html);pg.evaluate("Object.defineProperty(window,'localStorage',{configurable:true,value:{store:{},setItem(k,v){this.store[k]=v},getItem(k){return this.store[k]||null}}})");
 def shot(name):
  pg.evaluate("$('toast').style.display='none'");pg.wait_for_timeout(80);pg.screenshot(path=str(r/name))
 def apply(): pg.locator('#apply').click()
 # click real controls to verify all five first-use flows
 for typ in ['split','creative','translate','story','construct']:
  pg.evaluate(f"newProject('{typ}')");assert pg.locator('.setuphead h1').inner_text()=='Формат';pg.get_by_role('button',name='Далее →',exact=True).click();pg.evaluate('openSound()');assert pg.locator('#mt').inner_text()=='Звук';
  if typ=='construct':
   pg.evaluate("soundSwitchScope('scene')");pg.locator('#snv').fill('42');pg.evaluate("soundSwitchScope('global')");assert pg.locator('#snv').input_value()=='100';pg.evaluate("soundSwitchScope('scene')");assert pg.locator('#snv').input_value()=='42';pg.locator('#sm').check();pg.locator('#svon').check();pg.locator('#svscope').select_option('scene');pg.locator('#svmode').select_option('none');assert not pg.locator('#sharedVoice').is_visible();
  apply();pg.get_by_role('button',name='Далее →',exact=True).click();pg.evaluate("openSettings('subs')");assert not pg.locator('#subDetails').is_visible();pg.locator('#su').check();assert pg.locator('#subDetails').is_visible();pg.locator('#su').uncheck();apply();pg.get_by_role('button',name='Далее →',exact=True).click();pg.get_by_role('button',name='Сохранить шаблон',exact=True).click();assert pg.evaluate('tab')=='files';pg.evaluate('addDemo()');pg.evaluate('startAssembly()');assert pg.locator('#mt').inner_text()==('Нужны комментарии' if typ=='creative' else 'Готово к сборке');pg.evaluate('closeModal();go("home")');
 assert pg.evaluate('projects.length')==5;pg.evaluate("projects=JSON.parse(localStorage.getItem('clipflow-draft'));p=null;go('home')");assert pg.evaluate('projects.length')==5;pg.evaluate('openProject(4)');assert pg.evaluate('tab')=='files';assert pg.evaluate('p.settings.soundModel.scenes[Object.keys(p.settings.soundModel.scenes)[0]].nativeVolume')==42
 pg.evaluate('startSetup();p.format="16:9";cancelSetup()');assert pg.evaluate('p.format')=='9:16';
 pg.evaluate('go("home")');shot('14-projects.png');pg.evaluate('openProject(4);openSound()');shot('15-conditional-sound.png');pg.evaluate('closeModal();startAssembly()');shot('16-confirm.png');pg.evaluate('closeModal();openProject(1)');shot('17-creative-materials.png');pg.evaluate('startAssembly()');shot('18-comments-required.png');pg.evaluate('closeModal();selected=0;openSettings("comments")');shot('19-comments.png');pg.evaluate('closeModal();changeTab("edit")');pg.set_viewport_size({'width':390,'height':700});pg.wait_for_timeout(200);shot('20-mobile-creative.png');assert pg.evaluate('document.documentElement.scrollWidth')==390
 print('five first-use flows, persistence, sound dependency, cancel, comments guard, mobile:',errors);assert not errors;b.close()
