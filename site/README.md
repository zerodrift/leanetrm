# Default public page

This is the page the hosted gateway serves at `/` unless an installation
installs its own. It is part of the same Apache 2.0 licensed project as the
trading application; see [LICENSE](../LICENSE).

The page names the software, says the installation is run by its operator, and
links to the workspace, the source and the license. It carries no contact,
offer, illustration or media (D385 in the
[decision record](../docs/decisions.md)).

The files use project-owned HTML, CSS and an SVG icon. There are no JavaScript
or third-party runtime dependencies. They are separate from the signed-in
application's browser build. [headers.inc](headers.inc) allows styles, images
and media only from this site, so a page carries no inline styles and no
outside players.

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
| Operator-installed `images/NAME` | `/_landing/images/NAME` |
| `headers.inc` | Nginx include; never a public route |

## An installation's own page

An operator may install its own `index.html`, `site.css` and `favicon.svg` in
the gateway's landing folder, with images in its images folder.
That content is the installation's: it is never committed here, and its claims,
data and usage rights are the installation's responsibility.

The gateway holds any such page to the same rules. Styles come only from the
stylesheet. An image is served only under a single lowercase name of letters,
digits and hyphens ending in `webp`, `png` or `jpg`, and only as a regular file.
The gateway serves no films: a page shows one as a poster that links to the film
where it is hosted, and plays nothing itself:

```html
<a href="https://video.example.test/watch/NAME"><img src="/_landing/images/NAME.webp" alt="Film: what it shows" /></a>
```

See D382 and D388 for the images route and films.

`/app` remains protected by the application and opens its workspace. Static page
publication is a separate reviewed gateway operation; it does not change the
database, accounts or the application's image-only release envelope. See
[hosted pilot operations](../docs/hosted-pilot.md) for the routing and verification
contract. Do not deploy from a pull-request branch before review.
