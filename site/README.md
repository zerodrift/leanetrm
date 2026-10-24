# Public website

This is the source of the public LeanETRM landing page. It is part of the same
Apache 2.0 licensed project as the trading application; see [LICENSE](../LICENSE).
The page links to the public repository and distinguishes self-hosted software
from the hosted pilot, which requires an invited account. Its trading panel is
an illustration with synthetic prices.

The files use project-owned HTML, CSS and an SVG icon. There are no JavaScript
or third-party runtime dependencies. They are separate from the signed-in
application's browser build.

Public walkthrough links sit inside the hosting proxy's per-link `email_off`
exemption comments, so they remain direct mail links without an injected
decoding script.

The stylesheet URL carries its first 12 SHA-256 hex characters as a cache version.
Update it when the stylesheet changes; the content checks enforce the match.

Run the content and asset checks from the repository root:

```sh
python3 -m unittest discover -s tests -p test_public_site.py -v
```

The [hosted gateway template](../deploy/nginx.conf.template) consumes these files:

| Source | Public route |
| --- | --- |
| `index.html` | `/` |
| `site.css` | `/_landing/site.css` |
| `favicon.svg` | `/_landing/favicon.svg` |
| `headers.inc` | Nginx include; never a public route |

`/app` remains protected by the application and opens its workspace. Static page
publication is a separate reviewed gateway operation; it does not change the
database, accounts or the application's image-only release envelope. See
[hosted pilot operations](../docs/hosted-pilot.md) for the routing and verification
contract. Do not deploy from a pull-request branch before review.
