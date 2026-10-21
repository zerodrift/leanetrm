import test from 'node:test';
import assert from 'node:assert/strict';
import {csvSegments} from '../dist/interval-series-csv.js';

test('consecutive rows form one segment and gaps start another',()=>assert.deepEqual(
 csvSegments('start,value\r\n2027-01-01T05:00:00Z,0.25\r\n2027-01-01T06:00:00Z,1.50\n2027-01-01T08:00:00Z,0.75\n',60),
 [{start:'2027-01-01T05:00:00Z',values:['0.25','1.50']},{start:'2027-01-01T08:00:00Z',values:['0.75']}]));
test('fall-back repeated hours stay consecutive in UTC',()=>assert.equal(
 csvSegments('start,value\n2026-11-01T05:00:00Z,1\n2026-11-01T06:00:00Z,2\n',60).length,1));
test('local times are refused with the row number',()=>assert.throws(()=>csvSegments('start,value\n2027-01-01T05:00:00Z,1\n2027-01-01T06:00:00,2\n',60),/^Error: Row 3:/));
test('empty values and wrong headers are refused',()=>{
 assert.throws(()=>csvSegments('start,value\n2027-01-01T05:00:00Z,\n',60),/Row 2:/);
 assert.throws(()=>csvSegments('time,value\n',60),/Row 1:/);});

test('consecutiveness follows the series resolution',()=>{
 const text='start,value\n2027-01-01T05:00:00Z,1\n2027-01-01T05:15:00Z,2\n2027-01-01T05:45:00Z,3';
 assert.deepEqual(csvSegments(text,15),[{start:'2027-01-01T05:00:00Z',values:['1','2']},{start:'2027-01-01T05:45:00Z',values:['3']}]);
 assert.deepEqual(csvSegments(text,30),[{start:'2027-01-01T05:00:00Z',values:['1']},{start:'2027-01-01T05:15:00Z',values:['2','3']}]);});
test('offsets other than Z are refused, including a zero offset',()=>{
 assert.throws(()=>csvSegments('start,value\n2027-01-01T05:00:00+00:00,1\n',60),/^Error: Row 2:/);
 assert.throws(()=>csvSegments('start,value\n2027-01-01T05:00:00-05:00,1\n',60),/^Error: Row 2:/);});
test('a missing or an extra column is refused with its row number',()=>{
 assert.throws(()=>csvSegments('start,value\n2027-01-01T05:00:00Z\n',60),/^Error: Row 2:/);
 assert.throws(()=>csvSegments('start,value\n2027-01-01T05:00:00Z,1\n2027-01-01T06:00:00Z,2,3\n',60),/^Error: Row 3:/);
 assert.throws(()=>csvSegments('start,value\n\n2027-01-01T05:00:00Z,1\n',60),/^Error: Row 2:/);});
test('values are trimmed and otherwise sent exactly as written',()=>assert.deepEqual(
 csvSegments('start,value\n 2027-01-01T05:00:00Z , 0.2500 \n2027-01-01T06:00:00Z,-0\n2027-01-01T07:00:00Z,1e3',60),
 [{start:'2027-01-01T05:00:00Z',values:['0.2500','-0','1e3']}]));
test('the header is exact and a file without a final newline is accepted',()=>{
 assert.throws(()=>csvSegments('Start,Value\n2027-01-01T05:00:00Z,1\n',60),/^Error: Row 1:/);
 assert.throws(()=>csvSegments('',60),/^Error: Row 1:/);
 assert.deepEqual(csvSegments('start,value\n2027-01-01T05:00:00Z,1',60),[{start:'2027-01-01T05:00:00Z',values:['1']}]);});
test('a header alone yields no segments, leaving the empty import for the backend to refuse',()=>
 assert.deepEqual(csvSegments('start,value\n',60),[]));
