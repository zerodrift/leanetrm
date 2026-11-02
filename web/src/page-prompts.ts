import {PendingCommand} from './pending-command.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Version={page:string;version:number;instructions:string;recorded_at:string};
type Directory={pages:{id:string;name:string}[];configuration:{maximumLength:number;prompts:Version[]}};
export function promptRequest(page:string,expectedVersion:number,instructions:string){
  if(!page||!Number.isSafeInteger(expectedVersion)||expectedVersion<0)throw Error('Choose a page and reload its current prompt version.');
  return {page,expectedVersion,instructions};
}
export function setupPagePrompts(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const page=get<HTMLSelectElement>('ai-prompt-page'),instructions=get<HTMLTextAreaElement>('ai-prompt-instructions');
  const pending=new PendingCommand(code=>!!code&&['access_denied','request_format','page_prompt_input','page_prompt_conflict','page_prompt_version_conflict'].includes(code));
  let directory:Directory|undefined,version=0,generation=0,dirty=false;
  const storage=()=>`openetrm.assistant.page-prompt.${options.identity()}`;
  function store(){try{if(pending.command)sessionStorage.setItem(storage(),JSON.stringify(pending.command));else sessionStorage.removeItem(storage());}catch{/* This visit retains its request in memory. */}}
  function restore(){
    try{
      const saved=JSON.parse(sessionStorage.getItem(storage())??'null');
      if(saved?.path!=='settings/ai/page-prompts/save'||typeof saved.operationId!=='string'||!saved.body||
        typeof saved.body.page!=='string'||!Number.isSafeInteger(saved.body.expectedVersion)||typeof saved.body.instructions!=='string')return;
      pending.command={operationId:saved.operationId,path:saved.path,body:promptRequest(saved.body.page,saved.body.expectedVersion,saved.body.instructions),uncertain:true};
    }catch{/* An unreadable local receipt grants no authority. */}
  }
  function select(){
    const latest=directory?.configuration.prompts.filter(v=>v.page===page.value).sort((a,b)=>b.version-a.version)[0];
    version=latest?.version??0;instructions.value=latest?.instructions??'';dirty=false;
    get('ai-prompt-version').textContent=latest?`Version ${version}. New questions use this version; earlier questions keep their original prompt.`:'No page guidance configured. The application and agent instructions still apply.';
    sync();
  }
  function sync(){
    const busy=options.busy()||!!pending.command;
    page.disabled=busy;instructions.disabled=busy||!directory;
    get<HTMLButtonElement>('ai-prompt-save').disabled=busy||!directory||!dirty;
    get('ai-prompt-retry').hidden=!pending.command;
    get('ai-prompt-discard').hidden=!pending.command;
  }
  async function load(){
    if(!pending.command)restore();
    const started=++generation,identity=options.identity(),chosen=page.value;
    const next=await options.api<Directory>('settings/ai/page-prompts');
    if(started!==generation||identity!==options.identity())return;
    directory=next;instructions.maxLength=next.configuration.maximumLength;
    page.replaceChildren(...next.pages.map(p=>new Option(p.name,p.id)));
    if(next.pages.some(p=>p.id===chosen))page.value=chosen;
    select();
    if(pending.command){
      const saved=pending.command.body;
      page.value=String(saved.page);instructions.value=String(saved.instructions);version=Number(saved.expectedVersion);
      get('ai-prompt-version').textContent=`Unconfirmed save based on version ${version}. Retry sends this exact guidance with the original request identity.`;
      sync();
    }
  }
  async function send(){
    const command=pending.command,identity=options.identity();if(!command)return;
    const uncertain=pending.sending();store();
    try{await options.api(command.path,{...command.body,operationId:command.operationId});}
    catch(error){if(identity===options.identity()){pending.failed((error as {code?:string}).code,uncertain);store();}sync();throw error;}
    if(identity!==options.identity())return;
    pending.clear();store();dirty=false;get('ai-prompt-status').textContent='Page prompt saved.';sync();await load();
  }
  page.onchange=select;instructions.oninput=()=>{dirty=true;sync();};
  get<HTMLFormElement>('ai-prompt-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    if(pending.command)return;
    pending.begin('settings/ai/page-prompts/save',promptRequest(page.value,version,instructions.value));store();await send();
  });};
  get('ai-prompt-retry').onclick=()=>void options.act(send);
  get('ai-prompt-discard').onclick=()=>{if(options.busy())return;pending.clear();store();select();};
  return {load,sync,unconfirmed:()=>dirty||!!pending.command,reset(){generation++;directory=undefined;pending.clear();page.replaceChildren();instructions.value='';dirty=false;sync();}};
}
