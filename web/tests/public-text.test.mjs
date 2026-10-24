import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {readFileSync} from 'node:fs';
import {fileURLToPath} from 'node:url';

// Committed text is public: it cites decisions (D-numbers), never the internal rulings that led to them.
const root=fileURLToPath(new URL('../../',import.meta.url));
const textual=/\.(ts|mjs|js|cs|py|md|html|css|sql|json|yml|yaml|sh|txt|toml|csproj)$/;
const files=execFileSync('git',['ls-files','-z'],{cwd:root,maxBuffer:1<<26}).toString().split('\0').filter(f=>textual.test(f)&&!f.startsWith('web/src/vendor/'));
// The word "ruling" before an R-number, or an R-number of two digits standing alone, as a reference to a ruling.
const ruling=/ruling R\d+|(?:^|[^A-Za-z0-9_#.\/-])R[1-9]\d(?![A-Za-z0-9_-])/m;

test('committed text names decisions, not internal rulings',()=>{
 assert.ok(files.length>200,'the repository is listed');
 const found=files.flatMap(file=>readFileSync(root+file,'utf8').split('\n').flatMap((line,index)=>ruling.test(line)?[`${file}:${index+1}: ${line.trim().slice(0,100)}`]:[]));
 assert.deepEqual(found,[]);
});
