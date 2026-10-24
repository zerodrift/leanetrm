import test from 'node:test';
import assert from 'node:assert/strict';
import {matchesTableRow,columnValues} from '../dist/regular-table.js';
test('global search matches words across displayed cells without case sensitivity',()=>{assert.equal(matchesTableRow(['Jan 2021','Gas futures','3.05'],'GAS jan',[]),true);assert.equal(matchesTableRow(['Jan 2021','Gas futures','3.05'],'gas feb',[]),false);});
test('column filters combine with global search and each other',()=>{const row=['Jan 2021','Gas futures','3.05'];assert.equal(matchesTableRow(row,'2021',['Jan 2021','Gas futures','3.05']),true);assert.equal(matchesTableRow(row,'2021',['Feb 2021','Gas futures',null]),false);assert.equal(matchesTableRow(row,'2022',['Jan 2021','Gas futures',null]),false);});
test('filtering uses exact display strings without numeric parsing or rounding',()=>{assert.equal(matchesTableRow(['0.000000000000000001'],'000000000000000001',[]),true);assert.equal(matchesTableRow(['100000000000000000.01'],'100000000000000000.02',[]),false);assert.equal(matchesTableRow(['Jan 2021'],'',[null,'missing']),false);});
test('empty and whitespace filters retain all displayed rows',()=>{assert.equal(matchesTableRow(['Jan 2021','3.05'],'   ',[null,null]),true);});

test('column choices list exact distinct displayed values and match whole cells',()=>{assert.deepEqual(columnValues(['3','3.1','3','0.000000000000000001','']),['3','3.1','0.000000000000000001','']);assert.equal(matchesTableRow(['3.1'],'',['3']),false);assert.equal(matchesTableRow([''],'',['']),true);});

test('numeric filters compare exact decimal values and combine inequalities with text filters',()=>{
 const row=['Book A','100000000000000000.01','-0.000000000000000001'];
 assert.equal(matchesTableRow(row,'book',['Book A','> 100000000000000000.00','< 0'],[1,2]),true);
 assert.equal(matchesTableRow(row,'',['Book A','>= 100000000000000000.02',null],[1,2]),false);
 assert.equal(matchesTableRow(['3.10'],'',['= 3.1'],[0]),true);
 assert.equal(matchesTableRow(['3.1'],'',['!= 3.10'],[0]),false);
 assert.equal(matchesTableRow(['-2'],'',['<= -2'],[0]),true);
 assert.equal(matchesTableRow([''],'',['< 1'],[0]),false);
 assert.equal(matchesTableRow(['0'],'',['> nope'],[0]),false);
 assert.equal(matchesTableRow(['0'],'',[''],[0]),false);
 assert.equal(matchesTableRow([''],'',[''],[0]),true);
});
