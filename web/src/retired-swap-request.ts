import type {RequestCommand} from './pending-command.js';

const retired=new Set(['swaps/settlement/determine','swaps/settlement/payment','swaps/settlement/net']);
export const isRetiredSwapRequest=(path:string)=>retired.has(path);

/** Status recovery for actor-owned transport retained before action policies. */
export function retiredSwapRequest(host:HTMLElement,options:{api:<T>(path:string,body:unknown)=>Promise<T>;actor:()=>string;
 command:()=>RequestCommand|undefined;busy:()=>boolean;act:(work:()=>Promise<void>)=>Promise<void>;
 resolved:(command:RequestCommand)=>Promise<void>;dismissed:()=>void}){
 const root=document.createElement('section');root.id='retired-swap-request';
 const check=document.createElement('button');check.type='button';check.id='swap-earlier-status';check.textContent='Check earlier settlement';
 const dismiss=document.createElement('button');dismiss.type='button';dismiss.id='swap-earlier-dismiss';dismiss.textContent='Dismiss unrecorded request';
 const message=document.createElement('p');message.setAttribute('role','status');
 root.append(check,dismiss,message);host.append(root);
 let checking=false,missing='',shown='';
 const identity=(command:RequestCommand|undefined)=>JSON.stringify([options.actor(),command]);
 function sync(){
  const command=options.command(),key=identity(command);
  if(shown!==key){shown=key;missing='';message.textContent='Check the earlier request before recording another action in Lifecycle.';}
  root.hidden=!command||!isRetiredSwapRequest(command.path);
  check.disabled=checking||options.busy();dismiss.hidden=missing!==key;dismiss.disabled=checking||options.busy();
 }
 async function read(){
  const command=options.command();if(!command||!isRetiredSwapRequest(command.path)||checking||options.busy())return;
  const sent=structuredClone(command),key=identity(command);checking=true;missing='';sync();
  await options.act(async()=>{
   let accepted=false;
   try{
    const result=await options.api<{found?:boolean;operationId?:string;tradeId?:string}>('swaps/settlement/status',{operationId:sent.operationId});
    if(identity(options.command())!==key)return;
    if(result.found===false){missing=key;message.textContent='No accepted outcome was found. Dismiss this unrecorded request before reviewing a new action in Lifecycle.';return;}
    const trade=sent.path==='swaps/settlement/payment'?sent.body.tradeId:(sent.body.preview as {tradeId?:unknown}|undefined)?.tradeId;
    if(result.found!==true||result.operationId!==sent.operationId||typeof trade!=='string'||result.tradeId!==trade)
     throw Error('The status did not identify this exact earlier operation and trade.');
    accepted=true;await options.resolved(sent);
   }catch(error){if(accepted)throw error;if(identity(options.command())===key)message.textContent=`Status not confirmed: ${(error as Error).message} The original request remains retained.`;}
   finally{checking=false;sync();}
  });
 }
 check.onclick=()=>void read();
 dismiss.onclick=()=>{
  const command=options.command(),key=identity(command);
  if(!command||!isRetiredSwapRequest(command.path)||checking||options.busy()||missing!==key)return;
  options.dismissed();sync();
 };
 sync();return{sync,check:read};
}
