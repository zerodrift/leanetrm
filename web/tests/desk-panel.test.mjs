import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {positionRows} from '../dist/desk-model.js';
import {positionMode,positionIndex,tradeIndex,setupDeskPanel} from '../dist/desk-panel.js';
import {sameMode} from '../dist/side-panel.js';
import {setCurveNames} from '../dist/market-view.js';
const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url),'utf8'));
setCurveNames(fixture.curveNames);
// One trade that holds two bindings, as the architecture allows: the same trade at the same revision, listed once for each binding.
const [first]=fixture.holdings;
const second={...first,bindingId:'b0000000-0000-0000-0000-0000000000b2',quantity:'250'};
const other=fixture.holdings[1];
const read=[first,second,other];

test('the modes of one trade\'s two bindings differ, and each is found where the read has it',()=>{
 const a=positionMode(first),b=positionMode(second);
 assert.deepEqual(a,{kind:'position',tradeId:first.tradeId,bindingId:first.bindingId,revision:1});
 assert.equal(a.tradeId,b.tradeId);assert.ok(!sameMode(a,b));
 assert.equal(positionRows(read).length,3,'both bindings of the trade are rows');
 assert.equal(positionIndex(read,a),0);assert.equal(positionIndex(read,b),1);assert.equal(positionIndex(read,positionMode(other)),2);
 assert.equal(positionIndex([other,second,first],a),2,'wherever the read has it');
 assert.equal(positionIndex([first],b),-1,'a read without the binding does not hold its position');
 assert.equal(positionIndex(read,{...a,revision:2}),-1,'the position at another revision is another position');
});

// The desk's side of the panel, with the shell, the position view and the grid as plain recorders.
function desk({holdings=read,drawn=true,answer=async()=>true}={}){
 const log=[],notices=[];let shown=holdings,drawing=drawn,mode=null;
 const shell={current:()=>mode,async show(next){log.push(['shell.show',next.bindingId]);if(sameMode(next,mode))return true;const ok=await answer();if(ok)mode=next;return ok;},reset(){log.push(['shell.reset']);mode=null;}};
 const position={show:(at,tab)=>log.push(['position.show',at.index,...(tab?[tab]:[])]),update:at=>log.push(['position.update',at.index]),clear:()=>log.push(['position.clear'])};
 const grid={selectRow:index=>log.push(['grid.select',index]),focusRow:index=>log.push(['grid.focus',index])};
 const controller=setupDeskPanel({shell,position,holdings:()=>shown,input:index=>drawing&&index<shown.length?{index}:undefined,grid:()=>grid,notice:message=>notices.push(message)});
 return {controller,log,notices,mode:()=>mode,reads:next=>{shown=next;},draws:value=>{drawing=value;}};
}

test('each binding of one trade opens its own position, and marks its own row',async()=>{
 const state=desk();
 await state.controller.open(0);await state.controller.open(1);
 assert.deepEqual(state.log.filter(([what])=>what!=='shell.show'),[['position.show',0],['grid.select',0],['position.show',1],['grid.select',1]]);
 assert.deepEqual(state.mode(),positionMode(second));
});

test('pressing the row that is already open leaves the panel as it is, so its tab and its reads are kept',async()=>{
 const state=desk();
 await state.controller.open(0);state.log.length=0;
 await state.controller.open(0);
 assert.deepEqual(state.log,[['shell.show',first.bindingId]],'the shell is asked, which focuses the panel; the position view is not drawn again');
});

test('a person who discards what the panel held, while a newer read lands, gets the position of the read on screen now',async()=>{
 let answer;const state=desk({answer:()=>new Promise(resolve=>{answer=resolve;})});
 const opening=state.controller.open(0);
 state.reads([other,second,first]);
 answer(true);await opening;
 assert.deepEqual(state.log.filter(([what])=>what!=='shell.show'),[['position.show',2],['grid.select',2]],'the position is found in the newer read, not at the row pressed');
 assert.deepEqual(state.notices,[]);
});

test('a read that no longer holds the position closes the panel, and says so',async()=>{
 const state=desk();
 await state.controller.open(1);state.log.length=0;
 state.reads([first,other]);state.controller.sync();
 assert.deepEqual(state.log,[['shell.reset'],['position.clear'],['grid.select',null]]);
 assert.deepEqual(state.notices,['The position the panel showed is not in this read, so the panel was closed.']);
 state.log.length=0;state.controller.sync();
 assert.deepEqual(state.log,[],'a closed panel has nothing to follow');
});

test('a newer drawing of the same read redraws the position at the place it has in it', async()=>{
 const state=desk();
 await state.controller.open(0);state.log.length=0;
 state.reads([other,first,second]);state.controller.sync();
 assert.deepEqual(state.log,[['position.update',1],['grid.select',1]]);
});

test('a desk that draws no positions opens nothing, and closes the panel it has, as a read without its position does',async()=>{
 const none=desk({drawn:false});
 await none.controller.open(0);
 assert.deepEqual(none.log,[],'there is nothing to open a position from');
 const state=desk();
 await state.controller.open(0);state.log.length=0;
 state.draws(false);state.controller.sync();
 assert.deepEqual(state.log.map(([what])=>what),['shell.reset','position.clear','grid.select']);
 assert.equal(state.notices.length,1);
});

test('closing the panel gives focus back to the row it was opened from, once',async()=>{
 const state=desk();
 await state.controller.open(1);state.log.length=0;
 state.controller.closed();
 assert.deepEqual(state.log,[['position.clear'],['grid.select',null],['grid.focus',1]]);
 state.log.length=0;state.controller.closed();
 assert.deepEqual(state.log,[['position.clear'],['grid.select',null]],'no row to return to when none was open');
});

test('resetting drops the position without asking and without moving focus',async()=>{
 const state=desk();
 await state.controller.open(0);state.log.length=0;
 state.controller.reset();
 assert.deepEqual(state.log,[['shell.reset'],['position.clear'],['grid.select',null]]);
});

test('a question the person answers no to changes nothing, and a row the read does not hold opens nothing',async()=>{
 const state=desk({answer:async()=>false});
 await state.controller.open(0);
 assert.deepEqual(state.log.filter(([what])=>what!=='shell.show'),[]);
 const empty=desk();
 await empty.controller.open(9);await empty.controller.open(-1);
 assert.deepEqual(empty.log,[]);
});

test('a trade is found in a read by its id and kind, and by its binding when it holds several',()=>{
 assert.equal(tradeIndex(read,{tradeId:other.tradeId,instrumentType:other.instrumentType}),2);
 assert.equal(tradeIndex(read,{tradeId:first.tradeId,instrumentType:first.instrumentType,bindingId:second.bindingId}),1);
 assert.equal(tradeIndex(read,{tradeId:first.tradeId,instrumentType:first.instrumentType,bindingId:first.bindingId}),0);
});

test('a trade the read does not hold, or holds as several positions, is refused instead of guessed',()=>{
 assert.throws(()=>tradeIndex(read,{tradeId:first.tradeId,instrumentType:first.instrumentType}),{message:'That trade holds several positions in this read, so the panel cannot tell which one is meant.'});
 assert.throws(()=>tradeIndex(read,{tradeId:'missing',instrumentType:'future'}),{message:'That trade is not in this Blotter read. Choose the books it is in, then open it again.'});
 assert.throws(()=>tradeIndex(read,{tradeId:other.tradeId,instrumentType:first.instrumentType}),{message:'That trade is not in this Blotter read. Choose the books it is in, then open it again.'},'the same id of another kind is another trade');
 assert.throws(()=>tradeIndex(read,{tradeId:first.tradeId,instrumentType:first.instrumentType,bindingId:'unknown'}),{message:'That trade is not in this Blotter read. Choose the books it is in, then open it again.'});
});

test('opening a position can name the tab it opens on, and the tab is only the first drawing\'s',async()=>{
 const state=desk();
 await state.controller.open(0,'terms');
 assert.deepEqual(state.log.filter(([what])=>what==='position.show'),[['position.show',0,'terms']]);
 state.log.length=0;state.controller.sync();
 assert.deepEqual(state.log.filter(([what])=>what.startsWith('position')),[['position.update',0]],'a redraw keeps the tab the person is on');
});

test('the position the panel shows is the read\'s holding of its mode, or nothing',async()=>{
 const state=desk();
 assert.equal(state.controller.shown(),undefined,'a closed panel shows none');
 await state.controller.open(1);
 assert.equal(state.controller.shown(),second);
 state.reads([first]);
 assert.equal(state.controller.shown(),undefined,'a read without its position does not hold it');
});

test('a panel that moved to another mode forgets its position and its row, and leaves focus where it is',async()=>{
 const state=desk();
 await state.controller.open(1);state.log.length=0;
 state.controller.left();
 assert.deepEqual(state.log,[['position.clear'],['grid.select',null]]);
 state.log.length=0;state.controller.closed();
 assert.deepEqual(state.log,[['position.clear'],['grid.select',null]],'it was not closed from a row, so there is none to return to');
});
