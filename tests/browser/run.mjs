import {withOwnedBrowser} from './owned-browser.mjs';
import {run as administration} from './administration.mjs';
import {run as marketContext} from './market-context.mjs';
import {run as power} from './power.mjs';
import {checkProfileLifetime} from './profile-lifecycle.mjs';
import {run as futures} from './futures.mjs';
import {run as tradeEditor} from './trade-editor.mjs';
import {run as swaps} from './swaps.mjs';
import {run as fixings} from './fixings.mjs';
import {run as tableInteractions} from './table-interactions.mjs';
import {run as units} from './units.mjs';
import {run as parties} from './parties.mjs';
import {run as exchange} from './exchange.mjs';
import {run as risk} from './risk.mjs';
import {run as profileDialog} from './profile-dialog.mjs';
if(process.argv.length!==4&&!(process.argv.length===5&&['--units','--parties-power','--exchange','--risk','--swaps','--fixings'].includes(process.argv[4])))throw Error('Invoke through tools/run_preview.py --browser-check --browser /path/to/browser');
let total=0;
for(const [name,run] of (process.argv[4]==='--fixings'?[['fixing-aware swaps',fixings]]:process.argv[4]==='--swaps'?[['futures',futures],['financial swaps',swaps],['table interactions',tableInteractions]]:process.argv[4]==='--risk'?[['risk',risk]]:process.argv[4]==='--exchange'?[['exchange futures',exchange]]:process.argv[4]==='--units'?[['units and precision',units]]:process.argv[4]==='--parties-power'?[['power TOUs',power],['party editors',parties]]:[['market context and profile dialog',async browser=>{await marketContext(browser);await profileDialog(browser);}],['administration',administration],['power TOUs',power],['party editors',parties],['futures',futures],['risk',risk],['trade editor',tradeEditor],['financial swaps',swaps],['table interactions',tableInteractions],['units and precision',units]])){
  const passed=await withOwnedBrowser(process.argv[2],process.argv[3],run);total+=passed;console.log(`${passed} ${name} browser checks passed.`);
}
if(process.argv.length===4)total+=await checkProfileLifetime(process.argv[2],process.argv[3]);
console.log(`${total} owned browser checks passed.`);
