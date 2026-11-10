import {idle} from './owned-browser.mjs';
import {copyFile,mkdtemp} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';

export async function openPhysical({click,evaluate,wait},page){
 if(page==='facilities')await click('#nav-facilities');
 else{
  if(await evaluate("document.querySelector('#physical-commodity').hidden"))await click('#nav-delivery-setup');
  if(!await evaluate(`document.querySelector('#nav-${page}').getAttribute('aria-current')==='page'`))await click('#nav-'+page);
 }
 await wait(`!document.querySelector('#${page}-page').hidden&&${idle}`);
}

export async function run(a){
 const {command,evaluate,wait,click,persona,check,url,fitsWidth,shot}=a;
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-physical-workspace-'));console.log('PHYSICAL_WORKSPACE_VISUAL_ARTIFACTS='+artifacts);
 const capture=async name=>{await shot(name);await copyFile(join(dirname(process.argv[2]),name+'.png'),join(artifacts,name+'.png'));};
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('editor');
 await evaluate("window.__physicalReads=[];{const send=window.fetch;window.fetch=(path,options)=>{__physicalReads.push(new URL(String(path),location.href).pathname);return send(path,options);};}");
 await click('#nav-delivery-setup');
 await check('delivery setup is one Reference Data screen with a visible commodity choice',"document.querySelector('#page-title').textContent==='Delivery setup'&&document.querySelector('#page-section').textContent==='REFERENCE DATA'&&!document.querySelector('#physical-commodity').hidden&&document.querySelector('#nav-gas-setup').getAttribute('aria-current')==='page'&&document.querySelector('#nav-delivery-setup').getAttribute('aria-current')==='page'");
 await check('opening gas setup does not read hidden crude setup or facility quantities',"__physicalReads.includes('/api/gas-setup/catalog')&&!__physicalReads.some(path=>path.startsWith('/api/crude-setup/')||path.startsWith('/api/facilities/'))",'__physicalReads');
 await capture('physical-workspace-gas');
 await openPhysical(a,'crude-setup');
 await check('crude uses the same screen and selects only its own editor',"document.querySelector('#page-title').textContent==='Delivery setup'&&document.querySelector('#gas-setup-page').hidden&&!document.querySelector('#crude-setup-page').hidden&&document.querySelector('#nav-crude-setup').getAttribute('aria-current')==='page'&&document.querySelector('#nav-delivery-setup').getAttribute('aria-current')==='page'");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('delivery setup fits a phone',fitsWidth);
 await capture('physical-workspace-mobile');
 await persona('viewer');await click('#nav-delivery-setup');
 await check('delivery readers retain setup without receiving complete-pool access',"!document.querySelector('#physical-commodity').hidden&&!document.querySelector('#nav-gas-setup').hidden&&!document.querySelector('#nav-crude-setup').hidden&&document.querySelector('#nav-facilities').hidden");
}
