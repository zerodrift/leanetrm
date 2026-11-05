import {idle} from './owned-browser.mjs';

// Page.reload returns before navigation finishes. Readiness must belong to the new document.
export async function reloadPage({command,evaluate,wait}){
  const previous=await evaluate('performance.timeOrigin');
  await command('Page.reload');
  await wait(`performance.timeOrigin!==${previous}&&${idle}`);
}
