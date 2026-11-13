"""Content checks for the repository's default public page (D385)."""
from hashlib import sha256
from html.parser import HTMLParser
from pathlib import Path
import unittest
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1] / 'site'
SOURCE_URL = 'https://github.com/zerodrift/leanetrm'
MEDIA = {'img', 'video', 'audio', 'source', 'track', 'iframe', 'object', 'embed'}

class Page(HTMLParser):
    def __init__(self, text):
        super().__init__()
        self.links, self.assets, self.headings, self.scripts = [], [], [], []
        self.media, self.inline_styles = [], []
        self.feed(text)
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == 'a': self.links.append(a.get('href', ''))
        if tag == 'link' and a.get('rel') in ('stylesheet', 'icon'): self.assets.append(a['href'])
        if tag == 'h1': self.headings.append(tag)
        if tag == 'script': self.scripts.append(a)
        if tag in MEDIA: self.media.append(tag)
        if tag == 'style' or 'style' in a: self.inline_styles.append(tag)

class DefaultPublicPage(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = (ROOT / 'index.html').read_text()
        cls.page = Page(cls.text)

    def test_open_source_is_visible_and_links_to_the_repository_and_license(self):
        self.assertIn('Open source', self.text)
        self.assertIn(SOURCE_URL, self.page.links)
        self.assertIn(SOURCE_URL + '/blob/master/LICENSE', self.page.links)
        self.assertIn('Apache 2.0', self.text)

    def test_workspace_is_an_explicit_same_domain_link(self):
        self.assertIn('/app', self.page.links)
        self.assertTrue(all(not x.startswith('https://app.') for x in self.page.links))

    def test_default_page_carries_no_contact_offer_or_outside_destination(self):
        # An installation's own contact, offer and destinations belong on the page it installs (D385).
        elsewhere = [x for x in self.page.links
                     if not (x in ('/', '/app') or x.startswith('#') or x == SOURCE_URL or x.startswith(SOURCE_URL + '/'))]
        self.assertEqual(elsewhere, [])

    def test_default_page_loads_no_images_or_media(self):
        self.assertFalse(self.page.media, 'The default page installs no media; an installation adds its own (D382, D385, D388).')

    def test_software_copyright_notice_is_kept(self):
        self.assertIn('© 2026 Trusty Genius LLC', self.text.split('<footer', 1)[1])

    def test_styling_stays_in_the_public_stylesheet(self):
        self.assertFalse(self.page.inline_styles, 'headers.inc allows styles only from the public stylesheet.')

    def test_stylesheet_changes_have_a_new_cache_identity(self):
        version = sha256((ROOT / 'site.css').read_bytes()).hexdigest()[:12]
        self.assertIn('/_landing/site.css?v=' + version, self.page.assets)

    def test_default_page_is_readable_without_javascript_or_accounts(self):
        self.assertEqual(self.page.headings, ['h1'])
        self.assertFalse(self.page.scripts)
        self.assertIn('href="#main"', self.text)

    def test_all_public_assets_and_fragment_targets_exist(self):
        for url in self.page.assets:
            self.assertTrue(url.startswith('/_landing/'), url)
            self.assertTrue((ROOT / urlsplit(url).path.removeprefix('/_landing/')).is_file(), url)
        for link in self.page.links:
            if link.startswith('#'):
                self.assertIn(f'id="{link[1:]}"', self.text)

if __name__ == '__main__':
    unittest.main()
