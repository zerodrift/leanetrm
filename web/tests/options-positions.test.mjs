import {prepareAmountDisplay,shownAmounts,selectDisplayPrecision,initialDisplayPrecision} from '../dist/amount-display.js';
import {test,before} from 'node:test';
import assert from 'node:assert/strict';
import {positionTable} from '../dist/options-positions.js';

const figure=(amount,missing=null)=>({amount,missing});
const row=(series,right,strike,lots,state,figures,reason=null)=>({tradeId:'t-'+series,bindingId:'b-'+series,tradeReference:'TR-'+series,bookId:'bk',book:'Synthetic options',series,right,strike,
 openLots:lots,state,reason,...figures});
const full=(value,delta,gamma,vega,theta)=>({value:figure(value),delta:figure(delta),gamma:figure(gamma),vega:figure(vega),theta:figure(theta)});
const none=reason=>({value:figure(null,reason),delta:figure(null,reason),gamma:figure(null,reason),vega:figure(null,reason),theta:figure(null,reason)});
const reference={code:'usd',revision:{catalogId:'c',revisionId:'r'}};
const read={model:'m',held:[],
 groups:[
  {underlyingSeries:'ng_f27',expiryAt:'2027-01-26T19:30:00.0000000+00:00',product:'synthetic_on',productName:'Synthetic gas options',definitionRevisionId:'definition-a',sensitivities:{policy:'p',dayTo:'2026-10-17T17:00:00+00:00'},currency:'USD',currencyReference:reference,priceUnit:'USD/MMBtu',quantityUnit:'MMBtu',
   rows:[row('on_f27_p3','put','3','-2','analysed',full('-3000.000000000000000000','10000.000000000000000000','-30000.000000000000000000','-80.000000000000000000','30.000000000000000000')),
    row('on_f27_c3','call','3','2','analysed',{...full('4000.000000000000000000','10000.5','30000','80','-40.126'),vega:figure(null,'sensitivity_unstable')})],
   // Deliberately not the sum of the rows above: the page shows what the backend states and adds nothing itself.
   subtotal:{value:figure('1234.500000000000000000'),delta:figure('20000.000000000000000000'),gamma:figure('0.000000000000000000'),vega:figure(null,'incomplete'),theta:figure('-10.126000000000000000')}},
  {underlyingSeries:'cl_g27',expiryAt:'2027-02-23T19:30:00.0000000+00:00',product:'synthetic_lo',productName:'Synthetic crude options',definitionRevisionId:'definition-b',sensitivities:null,currency:'USD',currencyReference:reference,priceUnit:'USD/MMBtu',quantityUnit:'bbl',
   rows:[row('lo_g27_c70','call','70','4','notCalibrated',none('not_calibrated'),'not_calibrated')],
   subtotal:{value:figure(null,'incomplete'),delta:figure(null,'incomplete'),gamma:figure(null,'incomplete'),vega:figure(null,'incomplete'),theta:figure(null,'incomplete')}}],
 totals:[{currency:'USD',currencyReference:reference,value:figure(null,'incomplete'),theta:figure(null,'incomplete')}]};

before(async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 const formatted={'-3000.000000000000000000':'-3,000.000','4000.000000000000000000':'4,000.000','-80.000000000000000000':'-80.000','30.000000000000000000':'30.000','-40.126':'-40.126','1234.500000000000000000':'1,234.500','-10.126000000000000000':'-10.126','10000.000000000000000000':'10,000','10000.5':'10,000.5','-30000.000000000000000000':'-30,000','30000':'30,000','20000.000000000000000000':'20,000','0.000000000000000000':'0'};
 await prepareAmountDisplay(shownAmounts('options/positions',read),async body=>body.map(r=>formatted[r.value]??r.value));
});

test('positions keep the backend\'s groups and rows in its order, each figure formatted for display',()=>{
 const table=positionTable(read);
 assert.deepEqual(table.groups.map(g=>[g.underlyingSeries,g.product,g.quantityUnit,g.currency,g.rows.map(r=>r.series)]),
  [['ng_f27','synthetic_on','MMBtu','USD',['on_f27_p3','on_f27_c3']],['cl_g27','synthetic_lo','bbl','USD',['lo_g27_c70']]]);
 const [put,call]=table.groups[0].rows;
 assert.deepEqual([put.openLots,put.value.text,put.delta.text,put.gamma.text,put.vega.text,put.theta.text],['-2','-3,000.000','10,000','-30,000','-80.000','30.000']);
 assert.deepEqual([call.value.text,call.delta.text,call.theta.text],['4,000.000','10,000.5','-40.126']);
 assert.deepEqual(call.vega,{text:'—',missing:'sensitivity_unstable'});
});

test('subtotals and totals are the backend\'s figures, never recomputed, and a missing one is a dash with its reason',()=>{
 const table=positionTable(read),subtotal=table.groups[0].subtotal;
 assert.deepEqual([subtotal.value.text,subtotal.delta.text,subtotal.gamma.text,subtotal.theta.text],['1,234.500','20,000','0','-10.126']);
 assert.deepEqual(subtotal.vega,{text:'—',missing:'incomplete'});
 assert.deepEqual(table.totals,[{currency:'USD',currencyReference:reference,value:{text:'—',missing:'incomplete'},theta:{text:'—',missing:'incomplete'}}]);
});

test('a position whose product is not calibrated here has its lots, no figures, and names the product to calibrate',()=>{
 const crude=positionTable(read).groups[1].rows[0];
 assert.equal(crude.state,'notCalibrated');assert.equal(crude.openLots,'4');
 for(const cell of [crude.value,crude.delta,crude.gamma,crude.vega,crude.theta])assert.deepEqual(cell,{text:'—',missing:'not_calibrated'});
});
