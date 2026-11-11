// Select actual rows through the same filters and local pages the user sees.
export function publicationSelection(scope='#price-publications-editor'){
 return `document.querySelector(${JSON.stringify(scope+' #price-publications-library')})?.dataset.selectedPublication??''`;
}
async function findPublication(driver,match,{scope='#price-publications-editor'}={}){
 const {evaluate,click,wait,idle}=driver,library=scope+' #price-publications-library';
 await wait(`${idle}&&!!document.querySelector(${JSON.stringify(library+' .tabulator-row')})`);
 const clear=library+' .table-filter-clear';
 if(await evaluate(`!document.querySelector(${JSON.stringify(clear)}).hidden`))await click(clear);
 const first=library+' [data-page=first]',next=library+' [data-page=next]';
 if(await evaluate(`!document.querySelector(${JSON.stringify(first)}).disabled`)){
  await click(first);await wait(`document.querySelector(${JSON.stringify(library+' .tabulator-page.active')})?.dataset.page==='1'`);
 }
 for(let page=0;page<1000;page++){
  const key=await evaluate(`[...document.querySelectorAll(${JSON.stringify(library+' [data-publication-key]')})].find(n=>${match})?.dataset.publicationKey`);
  if(key)return key;
  if(await evaluate(`document.querySelector(${JSON.stringify(next)}).disabled`))break;
  const before=await evaluate(`document.querySelector(${JSON.stringify(library+' .tabulator-page.active')}).dataset.page`);
  await click(next);await wait(`document.querySelector(${JSON.stringify(library+' .tabulator-page.active')})?.dataset.page!==${JSON.stringify(before)}`);
 }
 throw Error('Publication is not visible in the table: '+match);
}
export async function selectPublication(driver,key,options={}){
 const scope=options.scope??'#price-publications-editor';
 await findPublication(driver,'n.dataset.publicationKey==='+JSON.stringify(key),options);
 await driver.click(scope+' [data-publication-key="'+key+'"]');
 if(options.waitSelected!==false)await driver.wait(`(${publicationSelection(scope)})===${JSON.stringify(key)}&&${driver.idle}`);
}
export async function selectPublicationByName(driver,name,options={}){
 const key=await findPublication(driver,'n.textContent==='+JSON.stringify(name),options);
 return selectPublication(driver,key,options);
}
