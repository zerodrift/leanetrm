import test from 'node:test';
import assert from 'node:assert/strict';
import {readOptionView} from '../dist/futures-option-view.js';

// An option's Details name what it is: its product, its underlying future and the futures trades it booked, never their keys or codes.
const h={tradeId:'5d4c1a2e-0000-4000-8000-000000000001',component:'option',tradeReference:'TR-000096',instrumentReference:'IN-000096',book:'Synthetic book',revision:'1',
 executedAt:'2026-10-16T18:00:00Z',booking:{tradeDate:'2026-10-16'},currency:'USD',exchange:{code:'synthetic_exchange',name:'Synthetic exchange'},
 product:'synthetic_on',productName:'Synthetic gas options',series:'synthetic_on_202701_c4.00',right:'call',strike:'4',expiryAt:'2026-12-28T19:30:00Z',
 underlyingSeries:'r_synthetic0opaque0series0code',underlyingName:'Synthetic gas futures · SG',deliveryFrom:'2027-01-01',deliveryTo:'2027-02-01',
 lifecycleState:'open',linkedFuturesTradeIds:['5d4c1a2e-0000-4000-8000-000000000002'],linkedFuturesTrades:['TR-000097'],actions:null};
const holding={tradeId:h.tradeId,lots:'5',openLots:'4',events:[{kind:'exercise',businessAt:'2026-11-02T20:00:00Z',lots:'1',remainingLots:'4',
 futuresTradeId:'5d4c1a2e-0000-4000-8000-000000000002',futuresTradeReference:'TR-000097'}]};
test('option details name the product, the underlying future and linked futures trades, and show no keys or codes',async()=>{
 const read=await readOptionView(async()=>holding,{snapshotId:'synthetic-close'},h,{writes:async()=>({enabled:false,reason:'Read only.'}),actor:()=>'trader',recorded:()=>{}});
 const view=read.view('UTC'),rows=[...view.terms,...view.history].flatMap(t=>t.rows),value=label=>rows.find(([name])=>name===label)?.[1];
 assert.equal(value('Product'),'Synthetic gas options');
 assert.equal(value('Underlying future'),'Jan 2027 · Synthetic gas futures · SG');
 assert.equal(value('Linked futures trades'),'TR-000097');
 assert.equal(value('Linked future'),'TR-000097');
 assert.equal(value('Series'),undefined);
 const shown=rows.map(([,v])=>String(v)).join(' ');
 for(const hidden of [h.tradeId,h.linkedFuturesTradeIds[0],h.underlyingSeries,h.series,h.product])assert.ok(!shown.includes(hidden),hidden);
});
