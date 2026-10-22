import {withOwnedBrowser} from './owned-browser.mjs';
import {run as administration} from './administration.mjs';
import {run as marketContext} from './market-context.mjs';
import {run as scheduleExamples} from './schedule-examples.mjs';
import {run as power} from './power.mjs';
import {checkProfileLifetime} from './profile-lifecycle.mjs';
import {run as futures} from './futures.mjs';
import {run as futuresAdmission} from './futures-admission.mjs';
import {run as futuresLoading} from './futures-loading.mjs';
import {run as tradeEditor} from './trade-editor.mjs';
import {run as swaps} from './swaps.mjs';
import {run as tableInteractions} from './table-interactions.mjs';
import {run as units} from './units.mjs';
import {run as parties} from './parties.mjs';
import {run as exchange} from './exchange.mjs';
import {run as risk} from './risk.mjs';
import {run as jobs} from './jobs.mjs';
import {run as startupRequests} from './startup-requests.mjs';
import {run as marketNavigationRead} from './market-navigation.mjs';
import {run as startupAccess} from './startup-access.mjs';
import {run as reference} from './reference.mjs';
import {run as referenceEditor} from './reference-editor.mjs';
import {run as liveCalculation} from './live-calculation.mjs';
import {run as customCurves} from './custom-curves.mjs';
import {run as curveActivation} from './curve-activation.mjs';
import {run as curveComposition} from './curve-composition.mjs';
import {run as contractDates} from './contract-dates.mjs';
import {run as marketDates} from './market-dates.mjs';
import {run as aiSettings} from './ai-settings.mjs';
import {run as agentSettings} from './agent-settings.mjs';
import {run as usageSettings} from './usage-settings.mjs';
import {run as assistantPanel} from './assistant-panel.mjs';
import {run as pilotClose} from './pilot-close.mjs';
import {run as notices} from './notices.mjs';
import {run as intervalSeries} from './interval-series.mjs';
import {run as intervalCurves} from './interval-curves.mjs';
async function marketNavigation(browser){await startupRequests(browser);await marketNavigationRead(browser);await startupAccess(browser);await reference(browser);await marketContext(browser);await scheduleExamples(browser);await referenceEditor(browser);}
async function tradingNavigation(browser){await futuresLoading(browser);await futuresAdmission(browser);await futures(browser);}
// The standard fixture initially has futures only. Book both product families
// through their editor checks before exercising mixed-portfolio risk.
const standardSuites=[['market navigation',marketNavigation],['administration',administration],['power TOUs',power],['party editors',parties],['futures',tradingNavigation],['trade editor',tradeEditor],['financial swaps',swaps],['risk',risk],['table interactions',tableInteractions],['units and precision',units],['saved-change notices',notices]];
if(process.argv.length!==4&&!(process.argv.length===5&&['--assistant','--pilot-close','--contract-dates','--units','--parties-power','--exchange','--risk','--jobs','--live-calculation','--custom-curves','--market-dates','--reference-editor','--interval-series','--interval-curves'].includes(process.argv[4])))throw Error('Invoke through tools/run_preview.py --browser-check --browser /path/to/browser');
let total=0;
for(const [name,run] of (process.argv[4]==='--live-calculation'?[['live calculation',liveCalculation]]:process.argv[4]==='--assistant'?[['page-assistant settings',aiSettings],['agents',agentSettings],['usage',usageSettings],['assistant panel',assistantPanel]]:process.argv[4]==='--interval-series'?[['interval series',intervalSeries]]:process.argv[4]==='--interval-curves'?[['interval curves',intervalCurves]]:process.argv[4]==='--pilot-close'?[['generated pilot closes',pilotClose]]:process.argv[4]==='--contract-dates'?[['shared contract dates',contractDates]]:process.argv[4]==='--reference-editor'?[['reference editor',referenceEditor]]:process.argv[4]==='--market-dates'?[['market close calendar',marketDates]]:process.argv[4]==='--custom-curves'?[['curve composition',curveComposition],['custom curves',customCurves],['curve activation',curveActivation]]:process.argv[4]==='--jobs'?[['jobs',jobs]]:process.argv[4]==='--risk'?[['risk',risk]]:process.argv[4]==='--exchange'?[['exchange futures',exchange]]:process.argv[4]==='--units'?[['units and precision',units]]:process.argv[4]==='--parties-power'?[['power TOUs',power],['party editors',parties]]:standardSuites)){
  const passed=await withOwnedBrowser(process.argv[2],process.argv[3],run);total+=passed;console.log(`${passed} ${name} browser checks passed.`);
}
if(process.argv.length===4)total+=await checkProfileLifetime(process.argv[2],process.argv[3]);
console.log(`${total} owned browser checks passed.`);
