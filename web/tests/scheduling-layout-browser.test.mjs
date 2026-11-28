import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const source=await readFile(new URL('../src/index.html',import.meta.url),'utf8');
const sidebar=source.match(/<aside[^>]*id="app-sidebar"[\s\S]*?<\/aside>/)?.[0];
assert.ok(sidebar,'the actual sidebar supplies the layout fixture');
const html='<link rel="stylesheet" href="/app.css">'+sidebar;
const app=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const menuClick=app.match(/\$\('sidebar-menus'\)\.addEventListener\('click',event=>\{[\s\S]*?\n\}(?:,true)?\);/)?.[0];
assert.ok(menuClick,'use the application menu click handler');
test('a usable desk control dismisses the phone menu, while a disabled control does not',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(html,async evaluate=>{
 const result=await evaluate(`(()=>{const compactNavigation={matches:true},$=id=>document.getElementById(id);let mobileMenuOpen=true;const renderLayout=()=>{};
  ${menuClick.replace('(event.target as Element)','event.target')}
  const button=document.createElement('button');button.className='nav-desk';button.innerHTML='<span>Crude</span>';$('scheduling-desks').append(button);
  button.disabled=true;button.click();const held=mobileMenuOpen;button.disabled=false;button.querySelector('span').click();
  return {held,open:mobileMenuOpen,focus:document.activeElement.id};})()`);
 assert.equal(result.held,true);
 assert.equal(result.open,false);
 assert.equal(result.focus,'sidebar-toggle');
}));
test('desk actions may disable themselves or focus a leave question after the phone menu closes',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(html,async evaluate=>{
 const result=await evaluate(`(async()=>{const {askLeave}=await import('/side-panel.js');const compactNavigation={matches:true},$=id=>document.getElementById(id);let mobileMenuOpen=true;const renderLayout=()=>{};
  ${menuClick.replace('(event.target as Element)','event.target')}
  const button=document.createElement('button');button.className='nav-desk';button.textContent='Gas';$('scheduling-desks').append(button);
  button.onclick=()=>{button.disabled=true;};button.click();const disabledActionOpen=mobileMenuOpen;
  const host=document.createElement('div');host.id='editor';host.tabIndex=-1;host.innerHTML='<div id="editor-prompt" hidden></div>';document.body.append(host);
  const controller=new AbortController();let decision;mobileMenuOpen=true;button.disabled=false;button.onclick=()=>{askLeave(host)(controller.signal).then(value=>{decision=value;});};button.click();
  const focus=document.activeElement.textContent;document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));await Promise.resolve();
  const kept=decision===false;controller.abort();return {disabledActionOpen,promptActionOpen:mobileMenuOpen,focus,kept};})()`);
 assert.deepEqual(result,{disabledActionOpen:false,promptActionOpen:false,focus:'Keep editing',kept:true});
}));
test('all desk controls fit one row and Scheduling labels remain readable at desktop, tablet and phone widths',
 {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(html,async(evaluate,command)=>{
 await evaluate(`(async()=>{const {setupSchedulingDesks}=await import('/scheduling-desks.js');
 const desks=setupSchedulingDesks({permissions:()=>['ReadMarketData','ReadTrades','ReadGasOperations'],owner:()=> 'synthetic-owner',storage:()=>sessionStorage,open(){},may:()=>true,refuse(){},busy:()=>false});
 document.querySelector('#nav-group-scheduling').hidden=false;document.querySelector('#nav-group-scheduling').open=true;desks.start();})()`);
 for(const width of [1280,1000,390]){
  await command('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:false});
  const layout=await evaluate(`new Promise(resolve=>requestAnimationFrame(()=>{const controls=[...document.querySelectorAll('#scheduling-desks button')],label=document.querySelector('#nav-facilities-transport .nav-text');
   resolve({rows:[...new Set(controls.map(e=>Math.round(e.getBoundingClientRect().top)))].length,
    controlsFit:controls.every(e=>e.scrollWidth<=e.clientWidth),labelFits:label.scrollWidth<=label.clientWidth});}))`);
  assert.equal(layout.rows,1,`desk controls at ${width}px`);
  assert.equal(layout.controlsFit,true,`desk names at ${width}px`);
  assert.equal(layout.labelFits,true,`full Scheduling labels at ${width}px`);
 }
}));
