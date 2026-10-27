import test from 'node:test';
import assert from 'node:assert/strict';
import {bookingRows,historyBooking} from '../dist/trade-annotations.js';

// A trade effective in December 2020 that was booked, and so stamped by the server (D129), in October 2026.
const backdated={traderId:'t1',traderRevision:1,trader:'Synthetic trader',traderCode:'trader',memo:null,executedAt:'2026-10-26T21:18:00Z',effectiveAt:'2020-12-17T16:00:00Z',
 tradeDate:'2026-10-26',legalEntityId:'e1',legalEntityRevision:1,counterpartyId:'c1',counterpartyRevision:1,legalEntity:'Synthetic entity 1',counterparty:'Synthetic party 1'};

test('the trade panel shows when the trade is effective beside when it was booked, and says the trade date and execution time are the booking',()=>{
 const rows=bookingRows(backdated,'America/Chicago'),labels=rows.map(([label])=>label),at=label=>rows.find(([l])=>l===label)?.[1];
 assert.equal(at('Effective'),'Dec 17, 2020, 10:00 AM');
 assert.equal(at('Trade date · when booked'),'2026-10-26');
 assert.equal(at('Executed · when booked'),'Oct 26, 2026, 4:18 PM');
 assert.deepEqual(labels.slice(labels.indexOf('Effective'),labels.indexOf('Effective')+3),['Effective','Trade date · when booked','Executed · when booked'],'the effective date sits next to the booking facts');
 assert.ok(!labels.includes('Trade date')&&!labels.includes('Executed'),'no booking fact is left unlabelled');
});

test('a revision\'s history names its trade date as the booking, beside its effective and recorded times',()=>{
 assert.ok(historyBooking(backdated).some(([label,value])=>label==='Trade date · when booked'&&value==='2026-10-26'));
});
