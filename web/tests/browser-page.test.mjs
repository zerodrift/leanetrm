import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('a page body starts on a page that has loaded its stylesheets and drawn a frame',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  // A stylesheet that arrives late, as one does on a busy machine. The page draws nothing until it has it.
  const late=createServer((request,response)=>setTimeout(()=>{response.setHeader('Content-Type','text/css');response.end('#host{width:321px}');},300));
  await new Promise(resolve=>late.listen(0,'127.0.0.1',resolve));
  try{
    await withBrowserPage(`<script>requestAnimationFrame(()=>{window.drawn=true;});</script><link rel="stylesheet" href="http://127.0.0.1:${late.address().port}/late.css"><div id="host"></div>`,async evaluate=>{
      assert.deepEqual(await evaluate("({width:document.querySelector('#host').getBoundingClientRect().width,drawn:window.drawn===true})"),{width:321,drawn:true});
    });
  }finally{await new Promise(resolve=>late.close(resolve));}
});
