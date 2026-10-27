import {test} from 'node:test';
import assert from 'node:assert/strict';
import {findJob} from '../dist/job-target.js';

// A directory of `total` jobs read 100 to a page, recording the pages read.
function directory(ids){
  const pages=[];
  return {pages,read:async page=>{pages.push(page);return {page,total:ids.length,jobs:ids.slice(page*100,page*100+100).map(id=>({id}))};}};
}

test('a job target opens the job on whichever page of the directory lists it, reading pages in order until it does',async()=>{
  const ids=Array.from({length:250},(_,n)=>`job-${n}`),pages=directory(ids);
  const found=await findJob('job-180',pages.read);
  assert.equal(found.job.id,'job-180');assert.equal(found.directory.page,1);assert.deepEqual(pages.pages,[0,1]);
});

test('a job the directory does not list is refused after its last page, never replaced by another job',async()=>{
  const pages=directory(['a','b']);
  await assert.rejects(findJob('missing',pages.read),/not listed on Jobs & Infrastructure/);
  assert.deepEqual(pages.pages,[0]);
  const empty=directory([]);
  await assert.rejects(findJob('missing',empty.read),/not listed/);
});
