type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Reference={turnId:string;step:number};
type Review={operation:string;page:string;publishesTo?:string|null;body:unknown;parameters:Record<string,string>;context:{mode:string;cutoff:string|null;frameId:string|null};status:string};

// Only explicit button activation sends an authorization. Nothing in a model response, stream event, automatic
// navigation, rendered request or earlier chat text can call it. The server re-reads the immutable proposal.
/** What authorizing a drafted issue report does that no other reviewed command does: it posts the text publicly. */
export function publicationNotice(review:{publishesTo?:string|null}):string|null{
  if(review.publishesTo===undefined)return null;
  return review.publishesTo===null?'This installation has no issue tracker connection, so authorizing this report sends nothing.'
    :`Authorizing posts this report to ${review.publishesTo}, where anyone can read it. Check that it leaves out trades, counterparties, prices and account details.`;
}

export function setupAssistantActions(options:{api:Api;identity:()=>string;changed:()=>Promise<void>}){
  let active:HTMLDialogElement|undefined;
  function close(){active?.close();active?.remove();active=undefined;}
  async function review(reference:Reference,signal?:AbortSignal,available:()=>boolean=()=>true){
    if(!available())throw Error('The page changed before Review opened.');
    const identity=options.identity();
    const value=await options.api<Review>('assistant/actions/review',reference);signal?.throwIfAborted();
    if(identity!==options.identity())return;
    if(!available())throw Error('The page changed before Review opened.');
    close();const dialog=document.createElement('dialog');dialog.className='assistant-action-review';active=dialog;
    const title=document.createElement('h2');title.textContent='Review assistant proposal';
    const summary=document.createElement('p');summary.textContent=`${value.operation.replaceAll('.',' › ').replaceAll('_v1','')} · awaiting your authorization`;
    const frame=document.createElement('p');frame.textContent=value.context.cutoff===null
      ?'This proposal used no financial context. The backend checks current permissions and versions when you authorize.'
      :`Prepared using knowledge through ${value.context.cutoff}. The backend checks current permissions and versions when you authorize.`;
    const payload=document.createElement('pre');payload.textContent=JSON.stringify({body:value.body,parameters:value.parameters},null,2);
    const consent=document.createElement('input');consent.type='checkbox';const label=document.createElement('label');label.append(consent,document.createTextNode(' I authorize this exact change.'));
    const authorize=document.createElement('button');authorize.type='button';authorize.textContent='Authorize change';authorize.disabled=true;
    const cancel=document.createElement('button');cancel.type='button';cancel.className='secondary';cancel.textContent='Close';cancel.onclick=close;
    const status=document.createElement('p');status.setAttribute('role','status');
    consent.onchange=()=>{authorize.disabled=!consent.checked;};
    let sending=false;
    authorize.onclick=async()=>{
      if(sending||!consent.checked||identity!==options.identity())return;
      sending=true;authorize.disabled=true;consent.disabled=true;cancel.disabled=true;
      status.textContent='Submitting the authorized command…';
      try{
        const result=await options.api('assistant/actions/authorize',reference);
        if(identity!==options.identity()){close();return;}
        status.textContent='The backend confirmed the command outcome.';payload.textContent=JSON.stringify(result,null,2);
        authorize.hidden=true;label.hidden=true;
        try{await options.changed();}catch{status.textContent='The backend confirmed the command. Refresh the page to see its updated data.';}
      }catch(error){
        if(identity!==options.identity()){close();return;}
        status.textContent=(error instanceof Error?error.message:'The outcome could not be confirmed.')+' Retry sends the same proposal and request identity.';
        authorize.textContent='Retry same authorized change';authorize.disabled=false;
      }finally{sending=false;cancel.disabled=false;}
    };
    const notice=publicationNotice(value),publication=document.createElement('p');publication.textContent=notice??'';publication.hidden=notice===null;
    dialog.append(title,summary,frame,publication,payload,label,authorize,cancel,status);document.body.append(dialog);dialog.showModal();
  }
  return {review,reset:close};
}
