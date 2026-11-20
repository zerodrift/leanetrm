export async function run({command,evaluate,wait,click,persona,check,idle,url}){
 await command('Page.enable');await command('Runtime.enable');
 await command('Page.navigate',{url});await wait("!!document.querySelector('#snapshot')&&"+idle);
 await persona('trader');
 await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
 await check('default Blotter opens with runtime context inputs',"document.querySelector('#error').hidden&&document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)').length>0");
 for(let n=0;n<3;n++){
  await click('#futures-refresh');
  await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
  await check('refresh '+(n+1)+' keeps independent position facts and valuations consistent',"document.querySelector('#error').hidden&&document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)').length>0");
 }
 await click('#desk-positions .tabulator-row:not(.tabulator-group)');
 await wait(idle);
 await check('position details can use the displayed read reference',"document.querySelector('#error').hidden");
 const selected=await evaluate("document.querySelector('#futures-page').dataset.pricingContext");
 await command('Page.reload');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
 await check('document reload opens a fresh valid working read',`document.querySelector('#error').hidden&&document.querySelector('#futures-page').dataset.pricingContext!==${JSON.stringify(selected)}`);
}
