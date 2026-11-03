import test from 'node:test';
import assert from 'node:assert/strict';
import {setupRiskJobs} from '../dist/risk-jobs-view.js';

function fixture(api){
 class Element{
  constructor(){this.children=[];this.own='';this.dataset={};}
  set textContent(value){this.own=value;this.children=[];}get textContent(){return this.own+this.children.map(c=>c.textContent).join('');}
  append(...children){this.children.push(...children);}replaceChildren(...children){this.own='';this.children=children;}get rows(){return this.children;}
 }
 const nodes=new Map();
 globalThis.document={createElement(){return new Element();},getElementById(id){if(!nodes.has(id))nodes.set(id,new Element());return nodes.get(id);}};
 return {view:setupRiskJobs({api,act:work=>work(),instant:at=>at}),nodes};
}
test('reset fences a delayed job directory from the previous account',async()=>{
 let resolve;const {view,nodes}=fixture(()=>new Promise(done=>{resolve=done;}));
 const loading=view.load();view.reset();resolve([]);await loading;
 assert.equal(nodes.get('risk-jobs-list').textContent,'');
});
test('an empty authorized directory has an explicit empty state',async()=>{
 const {view,nodes}=fixture(async path=>path.includes('/archive?')?{items:[],next:null}:[]);await view.load();
 assert.match(nodes.get('risk-jobs-list').textContent,/No recent risk jobs/);
});
test('a refused refresh clears previous report content and surfaces the failure',async()=>{
 const {view,nodes}=fixture(async()=>{throw Error('denied');});
 document.getElementById('risk-jobs-report').textContent='old report';
 await assert.rejects(view.load(),/denied/);
 assert.equal(nodes.get('risk-jobs-report').textContent,'');
 assert.match(nodes.get('risk-jobs-list').textContent,/could not be loaded/);
});

test('archive results from the previous account cannot repopulate the report directory',async()=>{
 let resolve;const {view,nodes}=fixture(async path=>path.includes('/archive?')?new Promise(done=>{resolve=done;}):[]);
 const loading=view.load();await Promise.resolve();await Promise.resolve();assert.ok(resolve);view.reset();resolve({items:[],next:null});await loading;
 assert.equal(nodes.get('risk-jobs-report').textContent,'');
});
