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
