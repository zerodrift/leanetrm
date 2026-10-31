// How the browser checks begin a new trade (D221): New trade opens the side panel on its four-product switch with nothing chosen, and a
// product is chosen there, which opens its form once what it needs has been read.
import {idle} from './owned-browser.mjs';

// The form each product opens, and the head the panel then shows.
const forms={futures:{form:'#futures-new-trade',head:'New future'},swaps:{form:'#swap-editing',head:'New swap'},ppa:{form:'#ppa-page',head:'New PPA'},gas:{form:'#gas-page',head:'New physical gas trade'}};

/** Presses New trade and waits for the panel to offer its switch. */
export async function startNewTrade({click,wait}){
  await click('#futures-new');
  await wait("document.querySelector('#position-details').dataset.mode==='new'&&!!document.querySelector('#new-trade-product')&&"+idle);
}

/** Opens PPA bookings as the sidebar has no entry for it: from the Blotter's New trade panel. */
export async function openPpaBookings({click,wait,evaluate}){
  try{
    await click('#nav-futures');
    await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+idle);
  }catch(error){
    const observed=await evaluate("({pricing:document.querySelector('#futures-page').dataset.pricingState,error:document.querySelector('#error').textContent,summary:document.querySelector('#futures-summary').textContent,booking:document.querySelector('#new-trade-availability').textContent})");
    throw Error(error.message+' · observed '+JSON.stringify(observed),{cause:error});
  }
  if(await evaluate("[...document.querySelectorAll('#product-trade-pending button')].some(b=>b.textContent==='Reconcile PPA request')")){
    await evaluate("[...document.querySelectorAll('#product-trade-pending button')].find(b=>b.textContent==='Reconcile PPA request').click()");await wait(idle);
  }else{await startNewTrade({click,wait});await click('#new-trade-ppa');}
  await wait("document.querySelector('#page-title').textContent==='Blotter'&&document.querySelector('#side-panel-form').contains(document.querySelector('#ppa-page'))&&"+idle);
}

/** Chooses a product in the open panel and waits for its form to be in the panel's form part. */
export async function chooseProduct({click,wait},product){
  await click('#new-trade-'+product);
  const {form,head}=forms[product];
  await wait(`(()=>{const form=document.querySelector(${JSON.stringify(form)});return !!form&&!form.hidden&&document.querySelector('#position-details-form').contains(form)&&!document.querySelector('#position-details-form').hidden&&document.querySelector('#side-panel-title').textContent===${JSON.stringify(head)};})()&&`+idle);
}

/** Begins a new trade of a product: New trade, then the product. */
export async function newTrade(page,product){
  await startNewTrade(page);
  await chooseProduct(page,product);
}

/**
 * Closes the panel a new trade is in, giving up what its form holds: a date chosen in a calendar is an edit, so closing it asks, and the
 * question is answered Discard changes. The checks that read the Blotter again with a form open begin here.
 */
export async function closeNewTrade({click,evaluate,wait}){
  await click('#details-collapse');
  if(await evaluate("!document.querySelector('#position-details-prompt').hidden"))await evaluate("document.querySelector('#position-details-prompt').querySelectorAll('button')[1].click()");
  await wait("document.querySelector('#position-details').hidden&&"+idle);
}
