import test from 'node:test';
import assert from 'node:assert/strict';
import {calendarSummary,visibleVersions,calendarKey,knownContractDateRejection} from '../dist/date-editor.js';
test('a reference calendar is described from its served weekend days, overrides and half-open coverage',()=>{
  const calendar={reference:{catalogId:'c',revisionId:'r',code:'test_business'},coverageFrom:'2019-01-01',coverageTo:'2022-01-01',weekendDays:['Saturday','Sunday'],closedDates:['2021-01-01','2021-03-15'],openDates:[]};
  assert.equal(calendarSummary(calendar),'Closed Saturday and Sunday · 2 closed dates · covers Jan 1, 2019 – Dec 31, 2021');
  assert.equal(calendarSummary({...calendar,weekendDays:[],closedDates:[],openDates:['2021-01-02']}),'No weekly closures · 1 opened date · covers Jan 1, 2019 – Dec 31, 2021');
});
test('readers see only published schedule versions; authors also see drafts',()=>{
  const versions=[{state:'draft'},{state:'published'}];
  assert.deepEqual(visibleVersions(versions,false),[{state:'published'}]);
  assert.deepEqual(visibleVersions(versions,true),versions);
});
test('calendar choices are keyed by exact reference version, not by code',()=>{
  assert.notEqual(calendarKey({catalogId:'c',revisionId:'r1',code:'business'}),calendarKey({catalogId:'c',revisionId:'r2',code:'business'}));
});
test('only refusals that prove nothing was written release a retained contract-date command',()=>{
  // An expired request token is refused as request_token, and a request the API cannot read, or one larger than it
  // accepts, before any handler runs.
  for(const code of ['contract_dates','contract_date_conflict','access_denied','request_token','request_format','request_too_large'])assert.equal(knownContractDateRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','request_cancelled','preview_error'])assert.equal(knownContractDateRejection(code),false,String(code));
});
