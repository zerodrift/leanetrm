import test from 'node:test';
import assert from 'node:assert/strict';
import fs,{existsSync,mkdtempSync,mkdirSync,readFileSync,readdirSync,realpathSync,rmSync,statSync,symlinkSync,writeFileSync} from 'node:fs';
import {syncBuiltinESMExports} from 'node:module';
import {tmpdir} from 'node:os';
import {dirname,join,relative,sep} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {build} from 'esbuild';
import {bundleApplication} from '../bundle-app.mjs';

test('initial HTML loads one application bundle and only the vendor modules it imports, the time chart\'s among them',()=>{
  const html=readFileSync(new URL('../dist/index.html',import.meta.url),'utf8');
  const preloads=[...html.matchAll(/<link rel="modulepreload" href="([^"]+)">/g)].map(match=>match[1]);
  assert.deepEqual(preloads.sort(),['/vendor/echarts.js','/vendor/klinecharts.js','/vendor/tabulator.js']);
  assert.equal((html.match(/<script type="module" src="\/app.js"><\/script>/g)??[]).length,1);
});

test('the emitted application has no remaining project-module imports',async()=>{
  const result=await build({absWorkingDir:fileURLToPath(new URL('../dist/',import.meta.url)),entryPoints:['app.js'],
    bundle:false,write:false,metafile:true,format:'esm',platform:'browser',logLevel:'silent'});
  const imports=Object.values(result.metafile.outputs).flatMap(output=>output.imports);
  assert.deepEqual(imports.map(item=>item.path).sort(),['./vendor/echarts.js','./vendor/klinecharts.js','./vendor/tabulator.js']);
});

async function fixture(files,run){
  const parent=realpathSync(mkdtempSync(join(tmpdir(),'openetrm-bundle-test-'))),root=join(parent,'dist');mkdirSync(root);
  try{
    for(const [name,source] of Object.entries({'package.json':'{"type":"module"}',...files})){
      const file=join(root,name);mkdirSync(dirname(file),{recursive:true});writeFileSync(file,source);
    }
    await run(root,parent);
  }finally{rmSync(parent,{recursive:true,force:true});}
}

const vendors={
  'vendor/echarts.js':`export const init=value=>'chart:'+value;`,
  'vendor/tabulator.js':`export class TabulatorFull{rows(){return ['a','b'];}}`,
};
test('bundling retains transitive application behavior and both external vendor consumers',async()=>{
  const entry=`import {render} from './chart.js'; import {rows} from './table.js'; export const report=()=>render(rows());`;
  await fixture({...vendors,
    'app.js':entry,
    'chart.js':`import {init} from './vendor/echarts.js'; import {label} from './nested/format.js'; export const render=rows=>init(label(rows));`,
    'nested/format.js':`export const label=rows=>rows.length+' rows';`,
    'table.js':`import {TabulatorFull} from './vendor/tabulator.js'; export const rows=()=>new TabulatorFull().rows();`,
  },async root=>{
    const metadata=await bundleApplication(root);
    assert.deepEqual(Object.keys(metadata.inputs).sort(),['app.js','chart.js','nested/format.js','table.js']);
    const application=await import(pathToFileURL(join(root,'app.js')).href);
    assert.equal(application.report(),'chart:2 rows');
    const first=readFileSync(join(root,'app.js'));
    writeFileSync(join(root,'app.js'),entry);await bundleApplication(root);
    assert.deepEqual(readFileSync(join(root,'app.js')),first,'identical emitted input must produce identical bundle bytes');
  });
});

test('the chart library is a declared vendor module that the bundle imports only when the application uses it',async()=>{
  const library=['vendor/klinecharts.js',`export const init=value=>'time:'+value;`];
  await fixture({...vendors,[library[0]]:library[1],'app.js':`import {init} from './vendor/klinecharts.js'; export const report=()=>init('x');`},async root=>{
    const metadata=await bundleApplication(root);
    assert.deepEqual(Object.values(metadata.outputs)[0].imports.map(item=>item.path),['./vendor/klinecharts.js']);
    assert.equal((await import(pathToFileURL(join(root,'app.js')).href)).report(),'time:x');
  });
  const built=readFileSync(new URL('../dist/vendor/klinecharts.js',import.meta.url),'utf8');
  assert.match(built,/KLineChart v10\.0\.3/);
  assert.doesNotMatch(built,/process\.env/,'a browser does not define process');
});

test('atomic publication stages outside the packaged site and cleans only its owned directory',async t=>{
  await fixture({...vendors,
    'app.js':`import {init} from './vendor/echarts.js'; import {TabulatorFull} from './vendor/tabulator.js'; export const report=()=>init(new TabulatorFull().rows());`,
  },async(root,parent)=>{
    const unrelated=join(parent,'.cache','unrelated-build');mkdirSync(unrelated,{recursive:true});
    writeFileSync(join(unrelated,'retained'),'another build');
    const rename=fs.renameSync,published=[];
    t.mock.method(fs,'renameSync',(source,target)=>{
      if(target===join(root,'app.js')){
        assert.ok(relative(root,source).startsWith('..'+sep),'temporary output must stay outside the packaged site');
        assert.equal(statSync(dirname(source)).dev,statSync(root).dev,'atomic publication must remain on the same filesystem');
        published.push(source);
      }
      return rename(source,target);
    });
    syncBuiltinESMExports();
    try{await bundleApplication(root);}finally{t.mock.restoreAll();syncBuiltinESMExports();}
    assert.equal(published.length,1);
    assert.equal(existsSync(dirname(published[0])),false,'owned staging directory must be removed after publication');
    assert.equal(readFileSync(join(unrelated,'retained'),'utf8'),'another build');
    assert.deepEqual(readdirSync(root).sort(),['app.js','package.json','vendor']);
  });
});

test('bundling rejects outside and symlinked private inputs before replacing the emitted entry',async()=>{
  for(const dependency of ['../private.js','./linked.js']){
    const entry=`import {init} from './vendor/echarts.js'; import {TabulatorFull} from './vendor/tabulator.js'; import '${dependency}'; export const report=()=>init(new TabulatorFull().rows());`;
    await fixture({...vendors,'app.js':entry},async(root,parent)=>{
      const outside=join(parent,'private.js');writeFileSync(outside,`throw Error('Private input');`);
      if(dependency==='./linked.js')symlinkSync(outside,join(root,'linked.js'));
      await assert.rejects(bundleApplication(root),/escapes the build directory/);
      assert.equal(readFileSync(join(root,'app.js'),'utf8'),entry);
    });
  }
});

test('a failed bundle leaves its emitted input untouched and rejects undeclared runtime imports',async()=>{
  for(const addition of [`import './missing.js';`,`export const = ;`,`import 'https://invalid.example/unexpected.js';`]){
    const source=`import {init} from './vendor/echarts.js'; import {TabulatorFull} from './vendor/tabulator.js'; export const report=()=>init(new TabulatorFull().rows()); ${addition}`;
    await fixture({...vendors,'app.js':source},async root=>{
      await assert.rejects(bundleApplication(root));
      assert.equal(readFileSync(join(root,'app.js'),'utf8'),source);
    });
  }
});

test('building the application preserves every vendored license and notice',()=>{
  for(const [source,target] of [
    ['tabulator-tables/LICENSE','TABULATOR-LICENSE'],['echarts/LICENSE','ECHARTS-LICENSE'],['echarts/NOTICE','ECHARTS-NOTICE'],
    ['zrender/LICENSE','ZRENDER-LICENSE'],['klinecharts/LICENSE','KLINECHARTS-LICENSE'],['klinecharts/NOTICE','KLINECHARTS-NOTICE'],
    ['klinecharts/licenses/LICENSE-lightweight-charts','KLINECHARTS-LICENSE-LIGHTWEIGHT-CHARTS'],['tslib/CopyrightNotice.txt','TSLIB-COPYRIGHT'],['tslib/LICENSE.txt','TSLIB-LICENSE'],
  ])assert.deepEqual(readFileSync(new URL('../dist/vendor/'+target,import.meta.url)),readFileSync(new URL('../node_modules/'+source,import.meta.url)));
});

test('the packaged site contains no build tool, native binary or staging output',()=>{
  const root=new URL('../dist/',import.meta.url),files=readdirSync(root,{recursive:true});
  assert.ok(files.includes('app.js')&&files.includes('vendor/echarts.js')&&files.includes('vendor/tabulator.js'));
  assert.deepEqual(files.filter(file=>/(^|[\\/])(node_modules|esbuild|\.app-bundle[^\\/]*)([\\/]|$)|\.node$|\.tmp$/.test(file)),[]);
});
