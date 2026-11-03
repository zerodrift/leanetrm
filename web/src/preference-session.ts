import {newPreferences,parsePreferences,validTimeZone,type Preferences} from './preferences.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Saved={version:number;preferences:Preferences};
type Command={ownerId:string;operationId:string;expectedVersion:number;preferences:Preferences};
type Owner={ownerId:string;identity:string;version:number;confirmed?:Preferences;pending?:Command;queue:Promise<void>;blocked:boolean;loaded:boolean;needsZoneChoice:boolean};
const changed=()=>Error('The account changed while preferences were being saved.');
const copy=(preferences:Preferences)=>parsePreferences(JSON.stringify(preferences));

/** One ordered writer per authenticated owner; uncertain requests retain their original identity and payload. */
export function preferenceSession(api:Api,warn:(message:string)=>void){
 let current:Owner|undefined;
 async function transmit(owner:Owner){
  if(current!==owner)throw changed();
  const command=owner.pending!;
  const saved=await api<Saved>('settings/preferences',command);
  if(current!==owner)throw changed();
  if(!saved||!Number.isInteger(saved.version)||saved.version<command.expectedVersion||saved.version>command.expectedVersion+1
    ||JSON.stringify(copy(saved.preferences))!==JSON.stringify(command.preferences))throw Error('The saved preferences do not match this request. Reload your profile.');
  owner.version=saved.version;owner.confirmed=copy(saved.preferences);owner.pending=undefined;owner.needsZoneChoice=false;
 }
 return {
  invalidate(){current=undefined;},
  async load(identity:string,zone:string):Promise<Preferences>{
   const owner:Owner={ownerId:'',identity,version:0,queue:Promise.resolve(),blocked:false,loaded:false,needsZoneChoice:false};current=owner;
   const read=await api<{ownerId:string;current:Saved|null}>('settings/preferences');
   if(!read||typeof read.ownerId!=='string'||!read.ownerId)throw Error('The preference owner is missing. Reload your profile.');
   const saved=read.current;owner.ownerId=read.ownerId;
   if(current!==owner)throw changed();
   if(saved!==null&&(!saved||!Number.isInteger(saved.version)||saved.version<1))throw Error('The saved preference version is invalid. Reload your profile.');
   const incompatible=saved!==null&&typeof saved.preferences?.timeZone==='string'&&!validTimeZone(saved.preferences.timeZone);
   const preferences=saved===null?newPreferences(zone):copy(incompatible?{...saved.preferences,timeZone:zone}:saved.preferences);
   if(incompatible){owner.needsZoneChoice=true;warn(`Your saved time zone is unsupported by this browser. This visit displays times in ${zone}. Choose and save a supported time zone in Profile.`);}
   owner.version=saved?.version??0;owner.confirmed=saved===null?undefined:structuredClone(saved.preferences);owner.loaded=true;
   return preferences;
  },
  save(preferences:Preferences,confirmZone=false):Promise<void>{
   const owner=current;if(!owner?.loaded)return Promise.reject(Error('Load your profile before saving preferences.'));
   const captured=copy(preferences);
   const saving=owner.queue.catch(()=>{}).then(async()=>{
    if(current!==owner)throw changed();
    if(owner.blocked)throw Error('Reload your profile before saving preferences that changed elsewhere.');
    if(owner.needsZoneChoice&&!confirmZone)throw Error('Choose and save a supported time zone in Profile before saving preferences.');
    try{
     if(owner.pending)await transmit(owner);
     if(JSON.stringify(owner.confirmed)===JSON.stringify(captured))return;
     owner.pending={ownerId:owner.ownerId,operationId:crypto.randomUUID(),expectedVersion:owner.version,preferences:captured};
     await transmit(owner);owner.needsZoneChoice=false;
    }catch(error){
     if(['user_preferences_conflict','user_preferences_owner_changed'].includes((error as {code?:string}).code??''))owner.blocked=true;
     throw error;
    }
   });
   owner.queue=saving;return saving;
  }
 };
}
