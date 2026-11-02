import {newTrade} from './new-trade.mjs';
export async function run(a){
 const {command,evaluate,wait,click,set,check,idle,url}=a;
 await command('Page.navigate',{url:url+'/'});await wait(idle);await a.persona('trader');await click('#nav-futures');await newTrade(a,'ppa');
 if(await evaluate("!document.querySelector('#ppa-frame').value")){const frame=await evaluate(`(async()=>{const frames=await(await fetch('/api/ppa/frames')).json();const matches=frames.filter(f=>!f.ppaBundle&&new Date(f.at).getTime()===new Date(${JSON.stringify(a.fixture?.baseFrameAt)}).getTime());if(matches.length!==1)throw Error('Select one exact synthetic PPA frame.');return matches[0].id;})()`);await set('#ppa-frame',frame);await wait(idle);}
 await check('new PPA starts with explicit independent composition and an empty side list',"document.querySelector('#ppa-composition').value==='composed_sides_v1'&&document.querySelectorAll('.ppa-component').length===0");
 await click('[data-add-role=energy_delivery]');
 await check('the plus control adds the first independently editable physical side',"document.querySelectorAll('.ppa-component').length===1&&!!document.querySelector('#ppa-component-0-quantity-kind')&&document.querySelector('.ppa-component .ticket-side-name').textContent==='Power'&&document.querySelector('#ppa-component-0-coefficient').value===''");
 await click('[data-add-role=certificate_delivery]');await click('[data-add-role=capacity_delivery]');await click('[data-add-role=fixed_charge]');
 await check('power REC capacity and fee coexist without selecting a different template',"document.querySelectorAll('.ppa-component').length===4&&[...document.querySelectorAll('.ppa-component .ticket-side-name')].map(n=>n.textContent).join(',')==='Power,REC,Capacity,Fee'&&document.querySelector('#ppa-composition').value==='composed_sides_v1'&&document.querySelector('#ppa-price-draft').disabled");
 await set('#ppa-clock','synthetic_eastern');await set('#ppa-cadence','60');
 await set('#ppa-component-0-quantity-kind','profile');
 await check('quantity mode shows only relevant profile controls',"!document.querySelector('#ppa-component-0-quantity-energy').closest('label').hidden&&document.querySelector('#ppa-component-0-quantity-sources').closest('label').hidden");
 await set('#ppa-component-3-quantity-kind','slots');await set('#ppa-component-3-quantity-slot',JSON.stringify({role:'energy_delivery',ordinal:0}));
 await evaluate("document.querySelector('.ppa-component [type=button].secondary').click()");
 await check('removing a referenced delivery side refuses without moving the fee link',"document.querySelectorAll('.ppa-component').length===4&&document.querySelector('#ppa-validation').textContent.includes('linked')&&JSON.parse(document.querySelector('#ppa-component-3-quantity-slot').value).role==='energy_delivery'");
}
