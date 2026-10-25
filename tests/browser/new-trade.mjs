// How the browser checks begin a new trade (D221): New trade opens the side panel on its Future | Swap switch with nothing chosen, and a
// product is chosen there, which opens its form once what it needs has been read.
import {idle} from './owned-browser.mjs';

// The form each product opens, and the head the panel then shows.
const forms={futures:{form:'#futures-new-trade',head:'New future'},swaps:{form:'#swap-editing',head:'New swap'}};

/** Presses New trade and waits for the panel to offer its switch. */
export async function startNewTrade({click,wait}){
  await click('#futures-new');
  await wait("document.querySelector('#side-panel').dataset.mode==='new'&&!!document.querySelector('#new-trade-product')&&"+idle);
}

/** Chooses a product in the open panel and waits for its form to be in the panel's form part. */
export async function chooseProduct({click,wait},product){
  await click('#new-trade-'+product);
  const {form,head}=forms[product];
  await wait(`(()=>{const form=document.querySelector(${JSON.stringify(form)});return !!form&&!form.hidden&&document.querySelector('#side-panel-form').contains(form)&&!document.querySelector('#side-panel-form').hidden&&document.querySelector('#side-panel-title').textContent===${JSON.stringify(head)};})()&&`+idle);
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
  await click('#side-panel-close');
  if(await evaluate("!document.querySelector('#side-panel-prompt').hidden"))await evaluate("document.querySelector('#side-panel-prompt').querySelectorAll('button')[1].click()");
  await wait("document.querySelector('#side-panel').hidden&&"+idle);
}
