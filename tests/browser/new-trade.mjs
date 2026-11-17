// How the browser checks begin a new trade (D221, D421): New trade opens its own page on the instrument chooser with nothing chosen, and
// an instrument is chosen there, which opens its form in the page once what it needs has been read. The page is the Blotter's section in
// its entry presentation (`#futures-page[data-entry]`), holding the same pane (`#position-details`) the Blotter's details use.
import {idle} from './owned-browser.mjs';

// The form each product opens, and the head the panel then shows.
const forms={futures:{form:'#futures-new-trade',head:'New future'},options:{form:'#option-page',head:'New futures option'},swaps:{form:'#swap-editing',head:'New swap'},ppa:{form:'#ppa-page',head:'New PPA'},gas:{form:'#gas-page',head:'New physical gas trade'},crude:{form:'#crude-page',head:'New physical crude trade'},lng:{form:'#lng-page',head:'New physical LNG cargo'},transport:{form:'#transport-page',head:'New Transport agreement'},'park-loan':{form:'#park-loan-page',head:'New Park and loan'}};

const entered="document.querySelector('#futures-page').hasAttribute('data-entry')&&document.querySelector('#position-details').dataset.mode==='new'&&!!document.querySelector('#new-trade-product')";
/** Whether New trade's page is shown, for a check to assert or wait on. */
export const onNewTradePage=`(${entered})`;

/**
 * Opens New trade's page and waits for its chooser: by the Blotter's own button where the Blotter is shown, by the sidebar's entry
 * anywhere else, and not at all where the page is shown already.
 */
export async function startNewTrade({click,wait,evaluate}){
  const ready=`(()=>{if(!(${idle}))return null;if(${entered})return 'shown';const blotter=document.querySelector('#futures-new'),sidebar=document.querySelector('#nav-new-trade');
    if(blotter?.checkVisibility())return blotter.disabled?null:'#futures-new';return sidebar&&!sidebar.hidden&&!sidebar.disabled?'#nav-new-trade':null;})()`;
  try{await wait(`${ready}!==null`);}
  catch(error){
    if(!evaluate)throw error;
    const observed=await evaluate("({booking:document.querySelector('#new-trade-availability')?.textContent,snapshot:document.querySelector('#snapshot')?.value,session:document.querySelector('#close-session')?.value,error:document.querySelector('#error')?.textContent})");
    throw Error(error.message+' · observed '+JSON.stringify(observed),{cause:error});
  }
  // The control is pressed in the page, as the check may have only `click` and `wait`: whichever of the two opens the page from here.
  await wait(`(()=>{const control=${ready};if(control===null)return false;if(control!=='shown')document.querySelector(control).click();return true;})()`);
  await wait(`${entered}&&`+idle);
}

/** Opens PPA bookings as the sidebar has no entry for it: from the Blotter, by its retained request's Reconcile or by New trade. */
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
  await wait("document.querySelector('#page-title').textContent==='New PPA'&&document.querySelector('#position-details-form').contains(document.querySelector('#ppa-page'))&&"+idle);
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
 * Leaves New trade's page for the Blotter, giving up what its form holds: a date chosen in a calendar is an edit, so leaving asks, and the
 * question is answered Discard changes. The checks that read the Blotter again after a form was open begin here.
 */
export async function closeNewTrade({click,evaluate,wait}){
  await click('#nav-futures');
  await wait("!document.querySelector('#position-details-prompt').hidden||document.querySelector('#page-title').textContent==='Blotter'");
  if(await evaluate("!document.querySelector('#position-details-prompt').hidden"))await evaluate("document.querySelector('#position-details-prompt').querySelectorAll('button')[1].click()");
  await wait("document.querySelector('#page-title').textContent==='Blotter'&&!document.querySelector('#futures-page').hasAttribute('data-entry')&&document.querySelector('#position-details').hidden&&"+idle);
}
