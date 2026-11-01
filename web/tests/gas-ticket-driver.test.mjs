import test from 'node:test';
import assert from 'node:assert/strict';
import {runInNewContext} from 'node:vm';
import {browserPageActions} from '../../tests/browser/owned-browser.mjs';
import {gasTicket} from '../../tests/browser/gas-ticket.mjs';

// A choice can arrive after the ordinary browser-action deadline but before the
// application's 20-second request deadline. Advance a clock instead of sleeping.
function delayedChoices(readyAt){
 let now=0;
 const selector='#gas-ticket .deal-ticket .ticket-strip .ticket-field[data-field="book"] .ticket-control';
 const options=()=>now>=readyAt?[{value:''},{value:'synthetic-book'}]:[{value:''}];
 const document={
  querySelectorAll:query=>query===selector+' option'?options():[],
  querySelector:query=>query===selector?{options:options()}:null,
 };
 const evaluate=async expression=>runInNewContext(expression,{document});
 const {wait}=browserPageActions(evaluate,{now:()=>now,pause:async ms=>{now+=ms;}});
 const selections=[];
 const ticket=gasTicket({evaluate,wait,set:async(query,value)=>selections.push({query,value,at:now})});
 return {ticket,selections,selector};
}

for(const method of ['first','choose'])test(`gas ${method} waits for a pending backend choice without inventing a value`,async()=>{
 const {ticket,selections,selector}=delayedChoices(12000);
 await ticket[method](null,'book','synthetic-book');
 assert.deepEqual(selections,[{query:selector,value:'synthetic-book',at:12000}]);
});

test('gas choice readiness stays bounded and reports the ticket state',async()=>{
 const {ticket,selections}=delayedChoices(Infinity);
 await assert.rejects(ticket.first(null,'book'),error=>/Timed out/.test(error.message)&&/observed/.test(error.message));
 assert.deepEqual(selections,[]);
});
