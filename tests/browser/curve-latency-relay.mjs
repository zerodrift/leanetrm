// Delay each byte in flight once in each direction. Calculation and earlier chunks
// can overlap the delay. Both sockets belong to the synthetic browser trial.
import {createServer,connect} from 'node:net';
import {once} from 'node:events';

export async function curveLatencyRelay(target){
  let oneWayMs=0;
  const sockets=new Set();
  const server=createServer({allowHalfOpen:true},client=>{
    const upstream=connect({host:'127.0.0.1',port:target.port,allowHalfOpen:true});
    for(const [source,dest] of [[client,upstream],[upstream,client]]){
      sockets.add(source);source.setNoDelay(true);
      const timers=new Set();
      let pending=0;
      function sendLater(send){
        if(!oneWayMs){send();return;}
        const timer=setTimeout(()=>{timers.delete(timer);if(!dest.destroyed)send();},oneWayMs);
        timers.add(timer);
      }
      source.on('data',bytes=>{
        pending+=bytes.length;
        if(pending>=512*1024)source.pause();
        sendLater(()=>{
          dest.write(bytes,()=>{
            pending-=bytes.length;
            if(pending<512*1024&&!source.destroyed)source.resume();
          });
        });
      });
      source.on('end',()=>sendLater(()=>dest.end()));
      source.on('error',()=>dest.destroy());
      source.on('close',()=>{
        sockets.delete(source);
        for(const timer of timers)clearTimeout(timer);
        dest.destroy();
      });
    }
  });
  server.listen(0,'127.0.0.1');await once(server,'listening');
  return {
    address:'http://127.0.0.1:'+server.address().port,
    setRoundTrip(ms){oneWayMs=ms/2;},
    async close(){const closed=once(server,'close');server.close();for(const socket of sockets)socket.destroy();await closed;}
  };
}
