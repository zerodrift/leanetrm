import type {CurveDefinition,CurveNode,PublishedSourceReference} from './custom-curve-model.js';

/** A presentation of the retained graph. Operations remain on their original nodes:
 * this projection neither compiles a different recipe nor resolves market dates. */
export type CurveObject={node:CurveNode;role:'curve'|'contracts'|'result'|'operation'};
export type ObjectConnection={id:string;source:string;target:string;port:string;operations:string[]};
export type CurveObjects={nodes:CurveObject[];connections:ObjectConnection[]};
const plumbing=new Set(['dates','sample']);
const resultKinds=new Set(['output','average','weighted_panels','lookup']);
export function curveObjects(definition:CurveDefinition):CurveObjects{
 const byId=new Map(definition.nodes.map(n=>[n.id,n])),used=new Set(definition.nodes.flatMap(n=>Object.values(n.inputs)));
 const nodes:CurveObject[]=definition.nodes.filter(n=>!plumbing.has(n.kind)||n.id===definition.output||!used.has(n.id)).map(node=>({node,
  role:node.kind==='source'||node.kind==='published_source'?'curve':node.kind==='select'||node.kind==='weighted_contracts'?'contracts':resultKinds.has(node.kind)?'result':'operation'}));
 const visible=new Set(nodes.map(n=>n.node.id)),connections:ObjectConnection[]=[];
 function support(id:string,path:Set<string>):string[]{
  const n=byId.get(id);if(!n||visible.has(id)||path.has(id))return [];
  const next=new Set(path).add(id);return [...Object.values(n.inputs).flatMap(input=>support(input,next)),id];
 }
 function walk(id:string,target:string,port:string,path:string[],seen:Set<string>){
  const n=byId.get(id);if(!n||seen.has(id))return;
  if(visible.has(id)){const operations=[...new Set([...path,target])];connections.push({id:JSON.stringify([id,target,port,path]),source:id,target,port,operations});return;}
  if(n.kind==='dates')return;
  const next=new Set(seen).add(id),extras=Object.values(n.inputs).filter(input=>byId.get(input)?.kind==='dates').flatMap(input=>support(input,next));
  for(const input of Object.values(n.inputs))walk(input,target,port,[...extras,id,...path],next);
 }
 for(const {node} of nodes)for(const [port,input] of Object.entries(node.inputs))walk(input,node.id,port,[],new Set([node.id]));
 return {nodes,connections};
}
export function curveObjectTitle(object:CurveObject,definition:CurveDefinition,sources:{code:string;name:string}[],published:{label:string;reference:PublishedSourceReference}[]):string{
 const n=object.node;
 if(n.kind==='source')return sources.find(s=>s.code===n.parameters.curve)?.name??'Source curve';
 if(n.kind==='published_source')return published.find(s=>JSON.stringify(s.reference)===JSON.stringify(n.publishedSource))?.label??'Published curve';
 if(n.id===definition.output)return definition.name||'Result curve';
 return object.role==='contracts'?'Contract set':object.role==='result'?'Result curve':n.kind==='dates'?'Date mapping':'Unconnected operation';
}
export function curveConnectionLabel(definition:CurveDefinition,connection:ObjectConnection,operators:{kind:string;label:string}[]):string{
 const target=definition.nodes.find(n=>n.id===connection.target),weight=target?.panelWeights?.find(w=>w.sourcePort===connection.port)?.weight;
 if(weight!==undefined)return 'Weight '+weight.replace(/^-/,'−');
 const steps=connection.operations.map(id=>definition.nodes.find(n=>n.id===id)).filter((n):n is CurveNode=>!!n&&n.kind!=='dates'&&n.kind!=='output');
 return steps.map(n=>operators.find(o=>o.kind===n.kind)?.label??n.kind).join(' → ')||'Pass through';
}
