/** Independent synthetic actor for real authority changes in owned UI checks. */
export async function sideSession(url,profile='admin'){
 const cookies=new Map();let csrf='';
 async function call(path,body){
  const response=await fetch(url+'/api/'+path,{method:body===undefined?'GET':'POST',headers:{Origin:url,...(cookies.size?{Cookie:[...cookies.values()].join('; ')}:{}),...(body===undefined?{}:{'Content-Type':'application/json','X-Preview-CSRF':csrf})},body:body===undefined?undefined:JSON.stringify(body)});
  for(const cookie of response.headers.getSetCookie()){const value=cookie.split(';')[0];cookies.set(value.slice(0,value.indexOf('=')),value);}
  const data=await response.json();if(data.csrfToken)csrf=data.csrfToken;
  if(!response.ok)throw Error('Synthetic authority operation failed: '+path+' '+response.status+' '+data.code);return data;
 }
 await call('bootstrap');await call('profile',{profile});
 async function replaceRoles(member,roles){return call('admin/update',{operationId:crypto.randomUUID(),id:member.id,expectedVersion:member.version,name:member.name,enabled:member.enabled,roles});}
 return{call,replaceRoles};
}

/** A real publication in the owned fixture, independent of the displayed selected pins. */
export async function correctSyntheticBase(actor,value){
 const data=await actor.call('workspace'),selected=data.frames.find(f=>f.at.startsWith('2020-12-18')&&data.initialFrameIds.includes(f.id));
 let fact=data.observations.find(o=>o.id==='70000000-0000-0000-0000-000000000001');
 while(data.observations.some(o=>o.corrects_id===fact.id))fact=data.observations.find(o=>o.corrects_id===fact.id);
 const picked=selected.observationIds.includes(fact.id)?selected:(await actor.call('select-observation',{snapshotId:selected.id,observationId:fact.id})).frame;
 await actor.call('corrections',{snapshotId:picked.id,observationId:fact.id,value});
}
