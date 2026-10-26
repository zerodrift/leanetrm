import test from 'node:test';
import assert from 'node:assert/strict';
import {refreshContextPanels} from '../dist/context-view.js';

// A minimal page: floating panels, each a summary and its coordinate inputs.
globalThis.HTMLSelectElement=class{};
const input=(coordinate,value)=>({value,dataset:{contextCoordinate:coordinate}});
function panel({mode,coordinates,named}){
 const values=named?{textContent:'stale'}:undefined;
 const summary={textContent:named?'Context Historical view · read only stale':'Context',querySelector:selector=>selector==='[data-context-values]'?values:null};
 return {dataset:mode?{contextMode:mode}:{},summary,values,querySelector:selector=>selector==='summary'?summary:null,querySelectorAll:()=>coordinates};
}
function render(...panels){globalThis.document={querySelectorAll:()=>panels};refreshContextPanels();}

test('the shared context keeps the knowledge it names and shows only its values beside it',()=>{
 const retained=panel({mode:'retained',named:true,coordinates:[input('Effective','2020-12-17'),input('Knowledge cutoff','2020-12-17T23:59:59')]});
 render(retained);
 assert.equal(retained.values.textContent,'Effective 2020-12-17 · Knowledge cutoff 2020-12-17 23:59:59');
 assert.equal(retained.summary.textContent,'Context Historical view · read only stale');
});
test('working data adds no values beside the knowledge the shared context names',()=>{
 const current=panel({mode:'current',named:true,coordinates:[input('Effective','2020-12-18'),input('Knowledge cutoff','2026-10-25T20:45:07')]});
 render(current);
 assert.equal(current.values.textContent,'');
});
test('a page\'s own context still states its values in its summary',()=>{
 const own=panel({coordinates:[input('Knowledge cutoff','2026-10-25T11:00:00')]});
 render(own);
 assert.equal(own.summary.textContent,'Context · Knowledge cutoff 2026-10-25 11:00:00');
});
