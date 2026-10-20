import {PendingCommand} from './pending-command.js';

const knownRejections=new Set(['job_conflict','job_input','job_transition','job_limit','access_denied','request_format','request_token','definition_rejected','infrastructure_conflict','infrastructure_input','infrastructure_busy']);

export class JobRequest extends PendingCommand {
  constructor(){super(code=>!!code&&knownRejections.has(code));}
}
