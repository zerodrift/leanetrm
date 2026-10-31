// The pop-out Blotter as the browser checks open it (blotter-workbench.md, Pop-out Blotter): Pop out pressed with the pointer, as a person
// presses it, since a window opens only on a person's gesture, and the window that opens attached once it has drawn its rows.
import {idle} from './owned-browser.mjs';

const q=selector=>`document.querySelector(${JSON.stringify(selector)})`;
/** Presses a control of `driver`'s page with the pointer. */
export async function press(driver,selector,clickCount=1){
  const {x,y}=await driver.evaluate(`(()=>{const r=${q(selector)}.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
  for(const type of ['mousePressed','mouseReleased'])await driver.command('Input.dispatchMouseEvent',{type,x,y,button:'left',buttons:type==='mousePressed'?1:0,clickCount});
}
/** The page target `opened` opens whose address starts with `prefix`, once there is one. */
export async function newTarget(command,opened,prefix){
  const before=(await command('Target.getTargets')).targetInfos.map(t=>t.targetId);
  await opened();
  for(let n=0;n<200;n++){
    const target=(await command('Target.getTargets')).targetInfos.find(t=>t.type==='page'&&!before.includes(t.targetId)&&t.url.startsWith(prefix));
    if(target)return target;
    await new Promise(resolve=>setTimeout(resolve,50));
  }
}
/** Pops the Blotter out of `driver`'s window (the main one unless named) and returns the pop-out's driver, once it has drawn its rows. */
export async function popOut({command,evaluate,url,tab},driver={command,evaluate}){
  const target=await newTarget(command,()=>press(driver,'#blotter-popout'),url+'/blotter.html');
  if(!target)throw Error('Pop out opened no Blotter window · '+JSON.stringify(await driver.evaluate(`({error:${q('#error')}.textContent,notice:${q('#notice')}.textContent})`)));
  const popup=await tab(target.targetId);
  // A browser opens the window at the size Pop out asks for; this headless one is given it.
  await popup.command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await popup.wait(`${idle}&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'`,60000).catch(async error=>{
    throw Error(error.message+' · popup '+JSON.stringify(await popup.evaluate("({url:location.href,busy:document.body.getAttribute('aria-busy'),pricing:document.querySelector('#futures-page').dataset.pricingState,rows:document.querySelectorAll('#desk-positions .tabulator-row').length,text:document.body.innerText.slice(0,1500)})")));});
  return popup;
}
