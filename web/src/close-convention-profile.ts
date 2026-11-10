type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type CloseConvention={code:string;name:string;state:string};
type Owner={identity:string;session:string|null;conventions:CloseConvention[];loaded:boolean;pending?:string;queue:Promise<void>};
const changed=()=>Error('The account changed while the close convention was being saved.');

/** A profile default uses the existing per-person choice; view overrides never write it. */
export function closeConventionProfile(api:Api){
 let current:Owner|undefined;
 async function send(owner:Owner,session:string){
  if(current!==owner)throw changed();
  const saved=await api<{session:string}>('close-sessions/choice',{session});
  if(current!==owner)throw changed();
  if(saved.session!==session)throw Error('The default close convention was not confirmed. Retry Save preferences.');
  owner.session=session;owner.pending=undefined;
 }
 return {
  invalidate(){current=undefined;},
  async load(identity:string){
   const owner:Owner={identity,session:null,conventions:[],loaded:false,queue:Promise.resolve()};current=owner;
   const [catalog,choice]=await Promise.all([api<{sessions:CloseConvention[]}>('close-sessions/catalog'),api<{session:string|null}>('close-sessions/choice')]);
   if(current!==owner)throw changed();
   if(choice.session!==null&&typeof choice.session!=='string')throw Error('The default close convention could not be read.');
   owner.session=choice.session;owner.conventions=catalog.sessions.filter(s=>s.state==='published');owner.loaded=true;
   return {session:owner.session,conventions:owner.conventions};
  },
  save(session:string|null){
   const owner=current;
   if(!owner?.loaded)return Promise.reject(Error('Load your profile before saving a close convention.'));
   if(session===null&&owner.session!==null)return Promise.reject(Error('Choose a published close convention.'));
   if(session!==null&&session!==owner.session&&!owner.conventions.some(c=>c.code===session))return Promise.reject(Error('Choose a published close convention.'));
   const saving=owner.queue.catch(()=>{}).then(async()=>{
    if(current!==owner)throw changed();
    if(owner.pending)await send(owner,owner.pending);
    if(session===null&&owner.session!==null)throw Error('Choose a published close convention.');
    if(session!==null&&session!==owner.session){owner.pending=session;await send(owner,session);}
   });
   owner.queue=saving;return saving;
  }
 };
}
