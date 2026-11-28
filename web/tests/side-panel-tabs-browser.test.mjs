import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

test('arrow keys move among the tabs that are shown',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="list"></div><div id="panel"></div>',async evaluate=>{
 await evaluate(`(async()=>{window.chosen=[];const {setupTabs}=await import('/side-panel.js');
  setupTabs(document.querySelector('#list'),[['a','A'],['b','B'],['c','C']],{label:'Synthetic tabs',panel:document.querySelector('#panel'),selected:'a',select:id=>chosen.push(id)});
  document.querySelector('#list-c').hidden=true;
  document.querySelector('#list-a').dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true}));
  document.querySelector('#list-b').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));})()`);
 assert.deepEqual(await evaluate('chosen'),['b','a']);
}));
