import {PendingCommand} from './pending-command.js';

const knownRejections=new Set(['job_conflict','job_input','job_transition','job_limit','pilot_profile','pilot_close_input','pilot_close_conflict','portfolio_policy','access_denied','request_format','request_too_large','request_token','definition_rejected','infrastructure_conflict','infrastructure_input','infrastructure_busy']);

export class JobRequest extends PendingCommand {
  constructor(){super(code=>!!code&&knownRejections.has(code));}
}
