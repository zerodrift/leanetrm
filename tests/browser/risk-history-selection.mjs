// The report editor is ready before the recent-job selector has received its options.
export async function selectRecentJob({wait,set},value,selector='#risk-job-history'){
  await wait(`(()=>{const list=document.querySelector(${JSON.stringify(selector)});return list&&!list.disabled&&[...list.options].some(o=>o.value===${JSON.stringify(value)});})()`);
  await set(selector,value);
}
