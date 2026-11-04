export type DatasetId='baseline'|'exchange'|'synthetic';
export function datasetId(value:string):DatasetId{
 if(value!=='baseline'&&value!=='exchange'&&value!=='synthetic')throw Error('Choose an available dataset.');
 return value;
}
export function datasetApi(id:DatasetId):string{datasetId(id);return id==='baseline'?'/api/':`/examples/${id}/api/`;}
export function selectedDataset(search:string):DatasetId{
 const values=new URLSearchParams(search).getAll('example');
 if(values.length>1)throw Error('Choose one dataset.');
 return datasetId(values[0]??'baseline');
}
/** Keep legacy primary recovery keys; other catalogs own independent retained commands. */
export function datasetScope(scope:string,id:DatasetId,catalog:string):string{
 datasetId(id);if(id==='baseline')return scope;
 if(!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(catalog)||/^0{8}(?:-0{4}){3}-0{12}$/.test(catalog))throw Error('The selected dataset identity is unavailable.');
 return `${scope}:${id}:${catalog}`;
}
