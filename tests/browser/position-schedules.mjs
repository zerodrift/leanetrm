import {blotterAt,decemberClose} from './gas-ticket.mjs';
export async function inspectPositionSide({evaluate,wait,set},slot,{host='#position-details-view',deliveryWindow}={}){
 const root=host+' .side-schedule-inspection';
 await wait(`!!document.querySelector(${JSON.stringify(root)})`,30000);
 await evaluate(`document.querySelector(${JSON.stringify(root)}).open=true`);
 await wait(`!!document.querySelector(${JSON.stringify(root+' .inspection-side option[value="'+slot+'"]')})`,30000);
 await set(root+' .inspection-side',String(slot));
 if(deliveryWindow!==undefined){
  await wait(`!!document.querySelector(${JSON.stringify(root+' .inspection-window option[value="'+deliveryWindow+'"]')})`,30000);
  if(await evaluate(`document.querySelector(${JSON.stringify(root+' .inspection-window')}).value`)!==deliveryWindow)await set(root+' .inspection-window',deliveryWindow);
 }
 await wait(`!!document.querySelector(${JSON.stringify(root+' .inspection-rows tbody tr')})`,30000).catch(async error=>{
  throw Error(error.message+' · '+await evaluate(`document.querySelector(${JSON.stringify(root+' [role=status]')})?.textContent`),{cause:error});
 });
}

export async function run(actions){
 const {command,evaluate,wait,click,set,persona,check,shot,idle,url}=actions;
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));
 await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
 await evaluate("document.querySelector('#desk-positions .desk-pill-swap').closest('.tabulator-row').click()");
 await wait("!!document.querySelector('#position-details-view .side-terms-list')");
 await check('position details show retained terms without a Curve tab or curve plot',"[...document.querySelectorAll('#position-details-tabs [role=tab]')].map(n=>n.textContent).join()==='Terms,Side schedules,Risk,History'&&!document.querySelector('#position-details-view .chart')");
 await click('#position-details-tabs-schedules');
 for(const slot of [0,1]){
  await inspectPositionSide({evaluate,wait,set},slot);
  await check('each native Side renders quantity, price, DF, signed cash and PV through the shared inspector',"(()=>{const grid=document.querySelector('#position-details-view .inspection-rows');return ['Quantity','Price','DF','Amount','PV','Lifecycle'].every(label=>[...grid.querySelectorAll('th')].some(h=>h.textContent===label))&&grid.querySelector('tbody tr')&&!document.querySelector('#position-details-tabs-curve')&&document.querySelector('#error').hidden;})()","document.querySelector('#position-details-view').innerText");
 }
 await click('#position-details-view .inspection-calculation');await wait("!!document.querySelector('#position-details-view .inspection-steps tbody tr')");
 await check('calculation detail retains averaging weights and native source inputs',"document.querySelector('#position-details-view .inspection-steps').textContent.toLowerCase().includes('weight')&&document.querySelector('#position-details-view .inspection-steps').textContent.includes('Inputs')");
 await evaluate("document.querySelector('#blotter-pane-details').closest('.dock-group').querySelector('.dock-maximize').click()");
 await shot('position-side-schedules');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
 await wait("document.documentElement.scrollWidth<=document.documentElement.clientWidth+1");
 await check('side schedules fit the narrow detail pane while their tables scroll inside it',"document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.querySelector('#position-details-tabs-schedules').getAttribute('aria-selected')==='true'");
}
