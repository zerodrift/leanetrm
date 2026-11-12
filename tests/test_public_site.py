from hashlib import sha256
from html.parser import HTMLParser
from pathlib import Path
import re
import unittest
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1] / 'site'
SOURCE_URL = 'https://github.com/zerodrift/leanetrm'
CONTACT = 'mailto:joe@trustygenius.com'
AUDIENCES = ('trading', 'risk', 'physical')
EMBEDS = {'audio', 'source', 'track', 'iframe', 'object', 'embed'}
# The film and poster names the gateway serves (deploy/nginx.conf.template).
FILM = r'/_landing/films/[a-z0-9][a-z0-9-]*\.mp4'
POSTER = r'/_landing/films/[a-z0-9][a-z0-9-]*\.(webp|jpg)'

def film_faults(video):
    """Why a video element would not play as intended under headers.inc and the gateway's film route."""
    return [name for name, wrong in [
        ('src', not re.fullmatch(FILM, video.get('src') or '')),
        ('poster', not re.fullmatch(POSTER, video.get('poster') or '')),
        ('controls', 'controls' not in video),
        ('preload', video.get('preload') != 'none'),
        ('autoplay', 'autoplay' in video)] if wrong]

class Page(HTMLParser):
    def __init__(self, text):
        super().__init__()
        self.links, self.assets, self.headings, self.scripts = [], [], [], []
        self.sections, self.section = [], None
        self.embeds, self.videos, self.inline_styles = [], [], []
        self.email_passthrough = False
        self.unmodified_email_links = []
        self.feed(text)
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        classes = (a.get('class') or '').split()
        if tag == 'section':
            self.section = {'id': a.get('id'), 'text': '', 'films': 0, 'videos': 0, 'contacts': [], 'headline': False}
            self.sections.append(self.section)
        if tag == 'a':
            self.links.append(a.get('href', ''))
            if self.email_passthrough:
                self.unmodified_email_links.append(a.get('href', ''))
            if self.section and a.get('href') == CONTACT: self.section['contacts'].append(classes)
        if tag == 'link' and a.get('rel') in ('stylesheet', 'icon'): self.assets.append(a['href'])
        if tag == 'img': self.assets.append(a['src'])
        if tag == 'h1':
            self.headings.append(tag)
            if self.section: self.section['headline'] = True
        if tag == 'script': self.scripts.append(a)
        if tag in EMBEDS: self.embeds.append(tag)
        if tag == 'video':
            self.videos.append(a)
            if self.section: self.section['videos'] += 1
        if tag == 'style' or 'style' in a: self.inline_styles.append(tag)
        if self.section and 'film' in classes: self.section['films'] += 1
    def handle_endtag(self, tag):
        if tag == 'section': self.section = None
    def handle_data(self, data):
        if self.section: self.section['text'] += data
    def handle_comment(self, text):
        if text.strip() == 'email_off': self.email_passthrough = True
        if text.strip() == '/email_off': self.email_passthrough = False

class PublicSite(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = (ROOT / 'index.html').read_text()
        cls.page = Page(cls.text)

    def section(self, name):
        found = [s for s in self.page.sections if s['id'] == name]
        self.assertEqual(len(found), 1, f'Expected one "{name}" section.')
        return found[0]

    def test_open_source_is_visible_and_links_to_the_repository_and_license(self):
        self.assertIn('Open source', self.text)
        self.assertIn(SOURCE_URL, self.page.links)
        self.assertIn(SOURCE_URL + '/blob/master/LICENSE', self.page.links)
        self.assertIn('Apache 2.0', self.text)

    def test_workspace_is_an_explicit_same_domain_link(self):
        self.assertGreaterEqual(self.page.links.count('/app'), 2)
        self.assertTrue(all(not x.startswith('https://app.') for x in self.page.links))

    def test_visitors_can_request_a_walkthrough_without_a_pilot_account(self):
        opening = [s for s in self.page.sections if s['headline']]
        self.assertEqual(len(opening), 1)
        self.assertTrue(any('button' in classes for classes in opening[0]['contacts']),
                        'Offer contact as a main action beside the headline.')
        self.assertIn('Request a walkthrough', opening[0]['text'])

    def test_navigation_reaches_every_named_section(self):
        for name in AUDIENCES + ('ai', 'security', 'services'):
            self.assertIn('#' + name, self.page.links)
            self.section(name)

    def test_installation_and_support_are_offered_with_their_own_contact(self):
        offer = self.section('services')
        self.assertIn('install', offer['text'].lower())
        self.assertIn('support', offer['text'].lower())
        self.assertTrue(offer['contacts'], 'Let a buyer reach the provider from the offer itself.')

    def test_assistant_pitch_keeps_the_human_approval_boundary(self):
        self.assertIn('approve', self.section('ai')['text'].lower())

    def test_remaining_work_is_stated_beside_the_paid_offer(self):
        self.assertIn('Still being built', self.section('services')['text'])

    def test_each_audience_has_three_films_as_placeholders_or_playable_videos(self):
        for name in AUDIENCES:
            audience = self.section(name)
            self.assertEqual(audience['films'], 3, name)
            self.assertEqual(audience['text'].count('Coming soon') + audience['videos'], 3, name)

    def test_film_rule_admits_only_gateway_films_that_wait_to_be_played(self):
        film = {'src': '/_landing/films/gas-operations.mp4', 'poster': '/_landing/films/gas-operations.webp',
                'controls': None, 'preload': 'none'}
        self.assertEqual(film_faults(film), [])
        for change, fault in [({'src': 'https://films.example.test/gas-operations.mp4'}, 'src'),
                              ({'src': '/_landing/films/Gas Operations.mp4'}, 'src'),
                              ({'src': '/_landing/films/gas-operations.mp4\n'}, 'src'),
                              ({'poster': '/_landing/images/gas-operations.webp'}, 'poster'),
                              ({'preload': 'auto'}, 'preload'), ({'autoplay': None}, 'autoplay')]:
            with self.subTest(change=change):
                self.assertEqual(film_faults({**film, **change}), [fault])
        self.assertEqual(film_faults({name: value for name, value in film.items() if name != 'controls'}), ['controls'])

    def test_films_on_the_page_play_from_the_gateway_and_wait_to_be_played(self):
        self.assertFalse(self.page.embeds, 'headers.inc allows media from this site only; name a film on its video element.')
        for video in self.page.videos:
            self.assertEqual(film_faults(video), [], video.get('src'))

    def test_styling_stays_in_the_public_stylesheet(self):
        self.assertFalse(self.page.inline_styles, 'headers.inc allows styles only from the public stylesheet.')

    def test_operator_and_support_provider_are_visible(self):
        footer = self.text.split('<footer', 1)[1]
        self.assertIn('© 2026 Trusty Genius LLC', footer)
        self.assertIn('Built and supported by Trusty Genius', footer)

    def test_public_email_links_do_not_require_an_edge_injected_script(self):
        mail = [x for x in self.page.links if x.startswith('mailto:')]
        self.assertEqual(set(mail), {CONTACT})
        self.assertEqual(self.page.unmodified_email_links, mail, 'Limit the edge exemption to the contact links.')
        self.assertFalse(self.page.email_passthrough, 'Limit the edge exemption to the contact links.')

    def test_stylesheet_changes_have_a_new_cache_identity(self):
        version = sha256((ROOT / 'site.css').read_bytes()).hexdigest()[:12]
        self.assertIn('/_landing/site.css?v=' + version, self.page.assets)

    def test_landing_is_readable_without_javascript_or_accounts(self):
        self.assertEqual(self.page.headings, ['h1'])
        self.assertFalse(self.page.scripts)
        self.assertIn('Synthetic illustration', self.text)
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
