# Public website

This is the source of the public LeanETRM landing page. It is part of the same
Apache 2.0 licensed project as the trading application; see [LICENSE](../LICENSE).
The page addresses trading, risk and physical-operations readers, links to the
public repository and distinguishes self-hosted software from the hosted pilot,
which requires an invited account. It offers paid installation and support from
the operator and states the remaining work beside that offer. Its panels are
illustrations with synthetic values.

The files use project-owned HTML, CSS and an SVG icon. There are no JavaScript
or third-party runtime dependencies. They are separate from the signed-in
application's browser build. [headers.inc](headers.inc) allows styles, images
and media only from this site, so the page carries no inline styles and no
outside players.

Images in `images/` belong to the repository and show synthetic values only.
`images/share.png` is the link-preview image. The gateway serves an image or a
film only under a single lowercase name of letters, digits and hyphens with a
listed extension, and only as a regular file; the content checks hold every
repository image to that rule.

Each audience section holds three demonstration films. A film that is not yet
published is a placeholder marked "Coming soon". Films and their posters are
operator content: they are installed in the gateway's `films/` folder, never
committed here, and their usage rights are the installation's responsibility.
To publish one, install `NAME.mp4` and its poster, then replace that tile's
poster block with:

```html
<video src="/_landing/films/NAME.mp4" poster="/_landing/films/NAME.webp" controls preload="none"></video>
```

and give the element its size in `site.css`. See D382 in the
[decision record](../docs/decisions.md).

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
| `images/NAME` | `/_landing/images/NAME` |
| Operator-installed `films/NAME` | `/_landing/films/NAME` |
| `headers.inc` | Nginx include; never a public route |

`/app` remains protected by the application and opens its workspace. Static page
publication is a separate reviewed gateway operation; it does not change the
database, accounts or the application's image-only release envelope. See
[hosted pilot operations](../docs/hosted-pilot.md) for the routing and verification
contract. Do not deploy from a pull-request branch before review.
