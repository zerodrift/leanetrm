import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
import {pivotCellFocus} from '../dist/assistant-selections.js';

test('a pivot ask copies native identities and excludes labels and values',()=>{
  const reference={revision:{catalogId:'catalog',revisionId:'revision'},code:'unit'};
  const group={groupId:'abc',bookId:'book',book:'Synthetic label',instrumentType:'future',quantitySpecification:reference,currencyReference:reference,executionPnl:'987654.321'};
  assert.deepEqual(pivotCellFocus(group,['book'],'executionPnl'),{keys:{groupId:'abc',bookId:'book',instrumentType:'future',quantitySpecification:reference,currencyReference:reference},measure:'executionPnl'});
  assert.doesNotMatch(JSON.stringify(pivotCellFocus(group,['book'],'executionPnl')),/987654|Synthetic label/);
});

test('three selected positions produce exactly their ids and no table evidence',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="height:500px"></div>',async evaluate=>{
    await evaluate(`(async()=>{
      const {renderDataGrid}=await import('/data-grid.js');
      window.ids=[1,2,3,4].map(n=>'71000000-0000-4000-8000-'+String(n).padStart(12,'0'));
      window.selected=[];
      window.grid=renderDataGrid(document.querySelector('#host'),'synthetic-selection','scope',['Reference','Value'],
        ids.map((id,n)=>['Synthetic cell '+n,'987654.321']),[1],()=>false,{keys:['tradeReference','executionPnl'],rowKeys:ids.map(id=>id+':binding'),
          layout:{order:[],widths:{},sort:[]},onLayout:()=>{},onRowOpen:()=>window.__openGate??true,onMultiSelection:keys=>{selected=keys;}});
      for(let n=0;n<100&&!grid.ready();n++)await new Promise(r=>setTimeout(r,20));
      if(!grid.ready())throw Error('Synthetic grid did not become ready.');
      for(const row of [...document.querySelectorAll('.tabulator-row')].slice(0,3))row.dispatchEvent(new MouseEvent('click',{bubbles:true,ctrlKey:true}));
      window.report=(await import('/assistant-pages.js')).pageReport({page:'futures',risk:false,trade:null,entry:null,lens:'values',
        view:{mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[]},pane:{details:false,charts:false},poppedOut:false,
        focus:{tradeIds:selected.map(key=>key.split(':')[0]),pivotCell:null}});
    })()`);
    const ids=await evaluate('ids.slice(0,3)'),report=await evaluate('report');
    assert.deepEqual(report.focus,{tradeIds:ids,pivotCell:null});
    assert.doesNotMatch(JSON.stringify(report),/987654|Synthetic cell|binding/);
    assert.equal(await evaluate("document.querySelectorAll('.tabulator-row[aria-selected=true]').length"),3);
    await evaluate(`(()=>{
      window.__openGate=new Promise(resolve=>window.__releaseOpen=resolve);
      const rows=[...document.querySelectorAll('.tabulator-row')];rows[0].click();
      rows[1].dispatchEvent(new MouseEvent('click',{bubbles:true,ctrlKey:true}));
      rows[3].dispatchEvent(new MouseEvent('click',{bubbles:true,ctrlKey:true}));
      window.__laterSelection=[...selected];__releaseOpen(true);
      return new Promise(resolve=>setTimeout(resolve,20));
    })()`);
    assert.deepEqual(await evaluate('selected'),await evaluate('__laterSelection'),'a late row-open reply cannot replace a newer selection');
  });
});


test('pivot menus require a currently picked cell, never a row background',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<div id="host" style="height:500px"></div>',async evaluate=>{
    await evaluate(`(async()=>{const {renderDataGrid}=await import('/data-grid.js');window.focusedCell=null;window.menuCount=0;
      window.grid=renderDataGrid(document.querySelector('#host'),'pivot-selection','scope',['Book','Value'],[['A','100'],['B','200']],[1],()=>false,
      {keys:['book','value'],rowKeys:['a','b'],layout:{order:[],widths:{},sort:[]},onLayout:()=>{},
      onCellPick:(r,c)=>{focusedCell=c===1?[r,c]:null;},onRowMenu:()=>{if(focusedCell)menuCount++;}});
      for(let n=0;n<100&&!grid.ready();n++)await new Promise(r=>setTimeout(r,20));
      const rows=[...document.querySelectorAll('.tabulator-row')];rows[0].querySelectorAll('.tabulator-cell')[1].click();
      rows[1].dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,button:2}));
    })()`);
    assert.equal(await evaluate('focusedCell'),null);assert.equal(await evaluate('menuCount'),0);
    await evaluate(`(()=>{const cells=document.querySelectorAll('.tabulator-row .tabulator-cell');cells[1].click();cells[2].click();})()`);
    assert.equal(await evaluate('focusedCell'),null,'a dimension cell clears the selected amount');
  });
});
