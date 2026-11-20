// Read references are transport metadata, never financial request fields. Symbols
// survive object spreads but are omitted by JSON and released with their view.
const references=Symbol('read references');
type Carrier={ [references]?:Record<string,string> };
const header='X-OpenETRM-Read-References';
const record=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
export function readReferences(value:unknown):Record<string,string>{
 const result:Record<string,string>={};
 function visit(item:unknown){
  if(!item||typeof item!=='object')return;
  for(const [id,proof] of Object.entries((item as Carrier)[references]??{})){
   // Renewal changes the proof bytes, while the server still verifies the
   // exact context and current workspace authority on every use.
   result[id]=proof;
  }
  for(const child of Object.values(item))visit(child);
 }
 visit(value);return result;
}
export function readReferenceHeaders(value:unknown):Record<string,string>{
 const metadata=readReferences(value);return Object.keys(metadata).length?{[header]:JSON.stringify(metadata)}:{};
}
export function attachReadReferences<T>(value:T,metadata:unknown):T{
 if(metadata==null)return value;
 const fields=typeof metadata==='string'?JSON.parse(metadata):metadata;
 if(!record(fields)||Object.keys(fields).length>8||Object.entries(fields).some(([id,proof])=>!/^[-0-9a-f]{36}$/i.test(id)||typeof proof!=='string'||proof.length>8192))throw Error('The read reference metadata is invalid. Refresh the view.');
 function visit(item:unknown){
  if(!item||typeof item!=='object')return;
  if(record(item)&&typeof item.contextId==='string'&&typeof fields[item.contextId]==='string')
   Object.assign(item,{[references]:{[item.contextId]:fields[item.contextId] as string}});
  for(const child of Object.values(item))visit(child);
 }
 visit(value);return value;
}
export function retainedKnowledge(source:{contextId?:string}){
 if(!source.contextId)throw Error('The selected read has no exact context. Refresh the view.');
 const metadata=(source as Carrier)[references];
 return {knowledgeMode:'retained' as const,contextId:source.contextId,...metadata?{[references]:metadata}:{}};
}
export function carryReadReferences<T>(value:T,source:unknown):T{
 const metadata=readReferences(source);
 if(value&&typeof value==='object'&&Object.keys(metadata).length)Object.assign(value,{[references]:metadata});
 return value;
}
export function cloneRead<T>(value:T):T{return carryReadReferences(attachReadReferences(structuredClone(value),readReferences(value)),value);}
