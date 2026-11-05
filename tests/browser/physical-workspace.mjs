import {idle} from './owned-browser.mjs';
import {copyFile,mkdtemp} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';

export async function openPhysical({click,evaluate,wait},page){
 if(await evaluate("document.querySelector('#physical-workspace').hidden"))await click('#nav-physical');
 if(!await evaluate(`document.querySelector('#nav-${page}').getAttribute('aria-current')==='page'`))await click('#nav-'+page);
 await wait(`!document.querySelector('#${page}-page').hidden&&${idle}`);
}

export async function run(a){
 const {command,evaluate,wait,click,persona,check,url,fitsWidth,shot}=a;
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-physical-workspace-'));console.log('PHYSICAL_WORKSPACE_VISUAL_ARTIFACTS='+artifacts);
 const capture=async name=>{await shot(name);await copyFile(join(dirname(process.argv[2]),name+'.png'),join(artifacts,name+'.png'));};
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('editor');
 await evaluate("window.__physicalReads=[];{const send=window.fetch;window.fetch=(path,options)=>{__physicalReads.push(new URL(String(path),location.href).pathname);return send(path,options);};}");
 await click('#nav-physical');
 await check('physical setup is one Operations workspace with a visible commodity choice',"document.querySelector('#page-title').textContent==='Facilities & delivery'&&document.querySelector('#page-section').textContent==='OPERATIONS'&&!document.querySelector('#physical-workspace').hidden&&document.querySelector('#nav-gas-setup').getAttribute('aria-current')==='page'&&!document.querySelector('#nav-group-settings #nav-gas-setup')&&!document.querySelector('#nav-group-settings #nav-crude-setup')");
 await check('planned facility workflows stay disabled after the app completes loading',"document.querySelectorAll('#physical-type-coverage [data-planned]').length===10&&[...document.querySelectorAll('#physical-type-coverage [data-planned]')].every(button=>button.disabled&&button.textContent.includes('Planned · TBD'))");
 await check('opening gas setup does not read hidden crude setup or facility quantities',"__physicalReads.includes('/api/gas-setup/catalog')&&!__physicalReads.some(path=>path.startsWith('/api/crude-setup/')||path.startsWith('/api/facilities/'))",'__physicalReads');
 await capture('physical-workspace-gas');
 await openPhysical(a,'crude-setup');
 await check('crude uses the same workspace and selects only its own editor',"document.querySelector('#page-title').textContent==='Facilities & delivery'&&document.querySelector('#gas-setup-page').hidden&&!document.querySelector('#crude-setup-page').hidden&&document.querySelector('#nav-crude-setup').getAttribute('aria-current')==='page'&&document.querySelector('#nav-physical').classList.contains('active')&&__physicalReads.includes('/api/crude-setup/catalog')");
 await click('#nav-market');await click('#nav-physical');
 await check('navigation and global busy-state changes never enable planned facility workflows',"[...document.querySelectorAll('#physical-type-coverage [data-planned]')].every(button=>button.disabled)");
 await check('the common sidebar entry restores the last physical section',"!document.querySelector('#crude-setup-page').hidden&&document.querySelector('#nav-crude-setup').getAttribute('aria-current')==='page'");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('the consolidated workspace fits a phone',fitsWidth);
 await capture('physical-workspace-mobile');
 await persona('viewer');await click('#nav-physical');
 await check('delivery readers retain setup without receiving complete-pool access',"!document.querySelector('#physical-workspace').hidden&&!document.querySelector('#nav-gas-setup').hidden&&!document.querySelector('#nav-crude-setup').hidden&&document.querySelector('#nav-facilities').hidden");
}
