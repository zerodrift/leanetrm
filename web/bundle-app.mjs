import {mkdirSync,mkdtempSync,realpathSync,renameSync,rmSync,writeFileSync} from 'node:fs';
import {dirname,isAbsolute,join,relative,resolve,sep} from 'node:path';
import {build} from 'esbuild';

export const vendorModules=Object.freeze(['./vendor/echarts.js','./vendor/tabulator.js','./vendor/klinecharts.js']);

export async function bundleApplication(directory){
  const root=realpathSync(directory);
  const result=await build({absWorkingDir:root,entryPoints:['app.js'],bundle:true,write:false,metafile:true,
    format:'esm',platform:'browser',target:'es2022',external:vendorModules,logLevel:'silent'});
  const outputs=Object.values(result.metafile.outputs),imports=outputs[0]?.imports??[];
  for(const file of Object.keys(result.metafile.inputs)){
    const path=relative(root,realpathSync(resolve(root,file)));
    if(path==='..'||path.startsWith('..'+sep)||isAbsolute(path))throw Error('Application bundle input escapes the build directory.');
  }
  if(result.outputFiles.length!==1||outputs.length!==1||new Set(imports.map(item=>item.path)).size!==imports.length||
      imports.some(item=>!item.external||item.kind!=='import-statement'||!vendorModules.includes(item.path)))
    throw Error('The application bundle must import only declared vendor modules, each once.');
  const cache=join(dirname(root),'.cache');mkdirSync(cache,{recursive:true});
  const staging=mkdtempSync(join(cache,'app-bundle-')),temporary=join(staging,'app.js');
  try{
    writeFileSync(temporary,result.outputFiles[0].contents,{flag:'wx'});
    renameSync(temporary,join(root,'app.js'));
  }finally{rmSync(staging,{recursive:true,force:true});}
  return result.metafile;
}
