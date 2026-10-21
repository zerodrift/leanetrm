import test from 'node:test';
import assert from 'node:assert/strict';
import {definiteRefusal} from '../dist/interval-series-view.js';
import {PendingCommand} from '../dist/pending-command.js';

function answered(code){
  const pending=new PendingCommand(definiteRefusal);
  pending.begin('interval-series/revise',{seriesId:'synthetic-series'},()=>'synthetic-operation');
  pending.failed(code,pending.sending());
  return pending.command;
}

test('a structured refusal from a series route releases its request, whatever its code',()=>{
  for(const code of ['clock_tzdb','clock_rule','interval_series_values','interval_series_revision_conflict','access_denied','numeric_format','request_format'])
    assert.equal(answered(code),undefined,code);
});

test('an unknown outcome keeps the exact request outstanding',()=>{
  for(const code of ['response_unknown','interval_series_outcome_unknown','request_failed',undefined]){
    const command=answered(code);
    assert.equal(command?.operationId,'synthetic-operation',String(code));
    assert.equal(command.uncertain,true);
  }
});
