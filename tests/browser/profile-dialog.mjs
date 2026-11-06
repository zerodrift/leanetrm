// The profile dialog is modal, so the page banner behind it is inert. Failures of
// its own actions must stay visible, announced and retryable inside it while open.
export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
  const message='We could not confirm the result. Refresh and check the saved changes before saving again.';
  const dialog="document.querySelector('#profile-dialog')",banner="document.querySelector('#error')",save="document.querySelector('#preferences-form [type=submit]')",change="document.querySelector('#open')";
  const shown=text=>`JSON.stringify([...${dialog}.querySelectorAll('#profile-error')].filter(n=>n.checkVisibility()).map(n=>n.textContent))===${JSON.stringify(JSON.stringify([text]))}`;
  const cleared=`[...${dialog}.querySelectorAll('#profile-error')].every(n=>n.hidden&&n.textContent==='')`;
  async function press(key,keyCode){for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key,code:key,windowsVirtualKeyCode:keyCode,...(type==='keyDown'&&key==='Enter'?{text:'\r'}:{})});}
  // Assistive technology must reach the dialog's own alert, unlike inert content behind the modal.
  async function announced(){
    const {result}=await command('Runtime.evaluate',{expression:`[...${dialog}.querySelectorAll('#profile-error')].find(n=>n.checkVisibility())`});
    if(!result.objectId)return false;
    const {nodes:[node]}=await command('Accessibility.getPartialAXTree',{objectId:result.objectId,fetchRelatives:false});
    return node.role.value==='alert'&&!node.ignored;
  }
  await command('Page.enable');await command('Runtime.enable');await command('Accessibility.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  // A blocked request waits for release, then fails as a dropped connection would.
  await evaluate(`(()=>{const send=window.fetch;window.__profileRequests=[];window.fetch=async(input,init)=>{window.__profileRequests.push(String(input));if(String(input).replace('/api/trading/workspace/stream','/api/trading/workspace')!==window.__block)return send(input,init);window.__block=null;await new Promise(resolve=>window.__release=resolve);window.__release=null;throw new TypeError('Synthetic blocked request');};})()`);
  async function block(path){await evaluate(`window.__block=${JSON.stringify(path)}`);await press('Enter',13);await wait('!!window.__release');}
  async function release(){await evaluate('window.__release()');await wait(idle);}
  await persona('trader');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')&&w.initialFrameIds.includes(f.id)).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await click('#profile-open');
  // Invalid nonempty preference still passes native required-field validation,
  // so the dialog's own validation, error announcement and retry are exercised.
  await evaluate(`{const zone=document.querySelector('#zone');zone.append(new Option('Synthetic invalid timezone','Synthetic/Invalid'));zone.value='Synthetic/Invalid';${save}.focus();}`);
  await press('Enter',13);await wait(idle);
  await check('invalid preferences show their message inside the open profile dialog, not the inert page banner',`${dialog}.open&&${shown('Choose a valid time zone.')}&&${banner}.hidden`);
  await check('the profile dialog alert reaches assistive technology',JSON.stringify(await announced()));
  await check('focus returns to Save preferences so Enter retries',`document.activeElement===${save}`);await shot('profile-error');
  await set('#zone','America/Chicago');await evaluate('window.__profileRequests=[]');await press('Enter',13);await wait(idle);
  await check('successful retry saves preferences, closes the dialog and clears its alert',`!${dialog}.open&&${cleared}&&${banner}.hidden&&document.querySelector('label[for=date-context-view] [data-user-time-zone]').textContent.includes('America/Chicago')`);
  await check('saving display preferences preserves the selected frame without reloading the Blotter',`document.querySelector('#snapshot').value===${JSON.stringify(frame)}&&!window.__profileRequests.some(p=>p==='/api/trading/workspace'||p==='/api/trading/workspace/stream')`);
  await click('#profile-open');if(!await evaluate("document.querySelector('#preview-access').open"))await click('#preview-access>summary');
  await set('#profile','viewer');await evaluate(`${change}.focus()`);
  await block('/api/profile');await release();
  await check('failed demo user switch shows its message inside the open profile dialog and keeps the current user',`${dialog}.open&&${shown(message)}&&${banner}.hidden&&document.querySelector('#profile-label').textContent==='Trader'`);
  await check('focus returns to Switch demo user so Enter retries',`document.activeElement===${change}`);
  await click('#profile-cancel');await wait(cleared);
  await check('closing the dialog clears its alert without moving the failure onto the page',`!${dialog}.open&&${cleared}&&${banner}.hidden&&document.querySelector('#status').textContent!=='Please check the message above.'`);
  await click('#profile-open');await set('#profile','viewer');await evaluate(`${change}.focus()`);
  await block('/api/profile');await press('Escape',27);await release();
  await check('a switch failing after the dialog was closed reports on the page banner',`!${dialog}.open&&${cleared}&&!${banner}.hidden&&${banner}.textContent===${JSON.stringify(message)}`);
  // Preferences belong to the server identity, independently of any old browser profile entries.
  await evaluate("for(const key of Object.keys(localStorage))if(key.startsWith('openetrm.preferences.v1.'))localStorage.removeItem(key)");
  await command('Page.navigate',{url});await wait(idle);
  await check('the server preference survives reload without browser preference storage',"document.querySelector('[data-user-time-zone]').textContent==='America/Chicago'");
  await click('#profile-open');await set('#zone','UTC');await evaluate(`${save}.click()`);await wait(idle);
  await check('the user explicitly restores UTC for subsequent independent browser cases',"!document.querySelector('#profile-dialog').open&&document.querySelector('[data-user-time-zone]').textContent==='UTC'");

}
