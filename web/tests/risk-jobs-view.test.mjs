import test from 'node:test';
import assert from 'node:assert/strict';
import {setupRiskJobs} from '../dist/risk-jobs-view.js';

function fixture(api){
 const nodes=new Map();
 globalThis.document={getElementById(id){if(!nodes.has(id))nodes.set(id,{textContent:'',replaceChildren(){this.textContent='';}});return nodes.get(id);}};
 return {view:setupRiskJobs({api,act:work=>work(),instant:at=>at}),nodes};
}
test('reset fences a delayed job directory from the previous account',async()=>{
 let resolve;const {view,nodes}=fixture(()=>new Promise(done=>{resolve=done;}));
 const loading=view.load();view.reset();resolve([]);await loading;
 assert.equal(nodes.get('risk-jobs-list').textContent,'');
});
test('an empty authorized directory has an explicit empty state',async()=>{
 const {view,nodes}=fixture(async()=>[]);await view.load();
 assert.match(nodes.get('risk-jobs-list').textContent,/No retained risk jobs/);
});
test('a refused refresh clears previous report content and surfaces the failure',async()=>{
 const {view,nodes}=fixture(async()=>{throw Error('denied');});
 document.getElementById('risk-jobs-report').textContent='old report';
 await assert.rejects(view.load(),/denied/);
 assert.equal(nodes.get('risk-jobs-report').textContent,'');
 assert.match(nodes.get('risk-jobs-list').textContent,/could not be loaded/);
});
