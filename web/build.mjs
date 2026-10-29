import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import {bundleApplication} from './bundle-app.mjs';
mkdirSync('dist', { recursive: true });
for (const file of ['index.html', 'app.css','position.html','position-window.css']) copyFileSync(`src/${file}`, `dist/${file}`);

mkdirSync('dist/vendor', {recursive:true});
for(const [source,target] of [
 ['node_modules/tabulator-tables/dist/js/tabulator_esm.min.js','tabulator.js'],
 ['node_modules/tabulator-tables/dist/css/tabulator.min.css','tabulator.css'],
 ['node_modules/tabulator-tables/LICENSE','TABULATOR-LICENSE'],
 ['node_modules/echarts/dist/echarts.esm.min.js','echarts.js'],
 ['node_modules/echarts/LICENSE','ECHARTS-LICENSE'],
 ['node_modules/echarts/NOTICE','ECHARTS-NOTICE'],
 ['node_modules/zrender/LICENSE','ZRENDER-LICENSE'],
 ['node_modules/klinecharts/LICENSE','KLINECHARTS-LICENSE'],
 ['node_modules/klinecharts/NOTICE','KLINECHARTS-NOTICE'],
 ['node_modules/klinecharts/licenses/LICENSE-lightweight-charts','KLINECHARTS-LICENSE-LIGHTWEIGHT-CHARTS'],
 ['node_modules/tslib/CopyrightNotice.txt','TSLIB-COPYRIGHT'],
 ['node_modules/tslib/LICENSE.txt','TSLIB-LICENSE'],
]) copyFileSync(source,'dist/vendor/'+target);

// The library's ES module reads process.env.NODE_ENV, which a browser does not define: state production, once, and nothing else.
const library=readFileSync('node_modules/klinecharts/dist/index.esm.js','utf8');
if(library.split('process.env.NODE_ENV').length!==2)throw Error('The chart library no longer reads process.env.NODE_ENV exactly once.');
writeFileSync('dist/vendor/klinecharts.js',library.replace('process.env.NODE_ENV',"'production'"));
for(const [name,page] of [['app.js','index.html'],['position-window.js','position.html']]){
 const metafile=await bundleApplication('dist',name);
 const imported=Object.values(metafile.outputs)[0].imports.map(item=>item.path);
 // Only the vendor modules this page imports are preloaded.
 const preloads=imported.map(file=>`<link rel="modulepreload" href="${file.slice(1)}">`).join('');
 const html=readFileSync('dist/'+page,'utf8'),entry=`<script type="module" src="/${name}"></script>`;
 if(html.split(entry).length!==2)throw Error('The built page must name one application module.');
 writeFileSync('dist/'+page,html.replace(entry,preloads+entry));
}
