import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {transform} from 'esbuild';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const source=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const html=await readFile(new URL('../src/index.html',import.meta.url),'utf8');

async function entry(context){
 const body=source.match(/async function useBusinessDateCurrent\(\)\{[^]*?\n\}/)?.[0];
 const handler=source.match(/\$\('use-business-date-current'\)\.onclick=[^\n]+/)?.[0];
 assert.ok(body,'the shared header needs an explicit current-knowledge operating-date action');
 assert.ok(handler,'the action must use the shared header entry point');
 vm.createContext(context);
 vm.runInContext((await transform(body+'\n'+handler,{loader:'ts',target:'es2022'})).code,context);
 return context;
}

test('the Facilities operating-date action preserves the explicit date through the shared leave guard without selecting a close',async()=>{
 assert.ok(/<button[^>]*id="use-business-date-current"[^>]*>Use business date with current knowledge<\/button>/.test(html),'the normal header must offer the explicit operating-date action');
 const date={value:'2021-01-15'},button={},calls=[];
 let allowed=false;
 const context={page:'facilities',$:id=>id==='date-context-on'?date:button,
  leaving:async work=>{calls.push('leave');if(allowed)await work();},
  currentInputs:async()=>{calls.push('current');date.value='';context.navigation={context:null};context.snapshot='';},
  dateEditor:{initialize:async day=>{calls.push(['initialize',day]);date.value=day;}},
  facilities:{load:async()=>{calls.push(['read',date.value]);assert.equal(context.navigation.context,null);assert.equal(context.snapshot,'');}},
  selectNewestClose:()=>assert.fail('physical authoring must not choose a financial close'),
  returnToWorking:()=>assert.fail('physical authoring must not replace the explicit operating date')};
 await entry(context);
 await button.onclick();assert.deepEqual(calls,['leave'],'a declined leave guard preserves the existing scope');
 allowed=true;calls.length=0;await button.onclick();
 assert.deepEqual(calls,['leave','current',['initialize','2021-01-15'],['read','2021-01-15']]);
 assert.equal(date.value,'2021-01-15');
});

test('the operating-date action refuses a missing explicit date and cannot run on a financial workspace',async()=>{
 const date={value:''},button={},calls=[];
 const context={page:'facilities',$:id=>id==='date-context-on'?date:button,leaving:work=>work(),
  currentInputs:async()=>calls.push('current'),dateEditor:{initialize:async()=>calls.push('initialize')},facilities:{load:async()=>calls.push('read')}};
 await entry(context);
 await assert.rejects(button.onclick(),/business date/i);assert.deepEqual(calls,[]);
 context.page='risk';date.value='2021-01-15';
 await assert.rejects(button.onclick(),/Facilities/i);assert.deepEqual(calls,[]);
});

test('the explicit operating-date actions remain usable in the normal phone header',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><main style="margin-left:0">'+html.match(/<header>[^]*?<\/header>/)[0]+'</main>',async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate("document.querySelector('#context-toolbar').hidden=false;document.querySelector('#context-actions').hidden=false;document.querySelector('#use-business-date-current').hidden=false;document.querySelector('#market-close-controls').hidden=true;document.querySelector('#business-date-field').hidden=false;document.querySelector('#date-context-on').type='date';document.querySelector('#date-context-on').value='2021-01-15';document.querySelector('#as-of-context').open=true;document.querySelector('#date-context').hidden=false;document.querySelector('#close-session-control').hidden=true");
 const geometry=await evaluate("({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,outside:[...document.querySelectorAll('#context-actions button')].filter(node=>{const box=node.getBoundingClientRect();return box.left<0||box.right>document.documentElement.clientWidth+1;}).map(node=>node.id)})");
 assert.ok(geometry.scroll<=geometry.width+1,JSON.stringify(geometry));assert.deepEqual(geometry.outside,[]);
}));
