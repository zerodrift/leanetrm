// A synthetic browser for the page helper's failure check. It publishes a debugger port and one page as a browser does, then fails
// the debugger connection in the way its first argument names. Like a browser blocked on a lock, it ignores a request to stop.
import {createHash} from 'node:crypto';
import {writeFileSync} from 'node:fs';
import {createServer} from 'node:http';
import {join} from 'node:path';

const [failure,record,...flags]=process.argv.slice(2);
if(!['never-opens','refuses','never-answers','closes'].includes(failure))throw Error('Unknown debugger failure: '+failure);
const profile=flags.find(flag=>flag.startsWith('--user-data-dir=')).slice('--user-data-dir='.length);
process.on('SIGTERM',()=>{});
// A check that is itself stopped must not leave this process running.
setTimeout(()=>process.exit(1),120000);

const debuggerPort=createServer((_request,response)=>response.end(JSON.stringify(
  [{type:'page',webSocketDebuggerUrl:`ws://127.0.0.1:${debuggerPort.address().port}/devtools/page/synthetic`}])));
debuggerPort.on('upgrade',(request,socket)=>{
  if(failure==='never-opens')return;
  if(failure==='refuses'){socket.destroy();return;}
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '+
    createHash('sha1').update(request.headers['sec-websocket-key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64')+'\r\n\r\n');
  // For 'never-answers' the connection stays open and every command goes unanswered.
  if(failure==='closes')socket.once('data',()=>socket.destroy());
});
debuggerPort.listen(0,'127.0.0.1',()=>{
  writeFileSync(record,JSON.stringify({pid:process.pid,profile}));
  writeFileSync(join(profile,'DevToolsActivePort'),debuggerPort.address().port+'\n/devtools/browser/synthetic\n');
});
