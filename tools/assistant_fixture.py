"""A qualified, enabled provider, priced and enabled model identities and published agents, made through Settings ›
AI as an administrator would, for the page-assistant checks; owned synthetic preview only."""
from uuid import uuid4
from check_agent_administration import RATES
from check_model_qualification import command as provider_command, terms
from check_model_versions import capabilities, command as model_command
from check_provider_tests import KEY

AGENTS, MODELS, PROVIDERS = 'settings/ai/agents', 'settings/ai/models', 'settings/ai/providers'


class Fixture:
    """Names everything it makes `Synthetic <label> …` with the run's stamp."""

    def __init__(self, admin, query, manifest, administrator, stamp, label):
        self.admin, self.query, self.administrator, self.stamp, self.label = admin, query, administrator, stamp, label
        self.usd = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','usd'"

    def provider(self):
        return next(p for p in self.admin.call(PROVIDERS)['providers'] if p['vendor'] == 'anthropic')

    def provider_change(self, action):
        return self.admin.call(f'{PROVIDERS}/{action}', provider_command('anthropic', self.provider()['version']))

    def replace_key(self):
        """A new key leaves the provider unattested and off."""
        self.admin.call(f'{PROVIDERS}/key', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': self.provider()['version'], 'key': KEY})

    def qualify(self):
        """A new key and terms leave the provider unattested and off; attesting and enabling qualify it again."""
        self.replace_key()
        self.admin.call(f'{PROVIDERS}/terms', terms('anthropic', self.provider()['version']))
        self.provider_change('attest'); self.provider_change('enable')

    def row_version(self, model_id):
        return int(self.query(f"SELECT version FROM assistant.models WHERE id='{model_id}';"))

    def version_of(self, model_id):
        return int(self.query(f"SELECT max(version) FROM assistant.model_versions WHERE model_id='{model_id}';"))

    def edit(self, model_id):
        self.admin.call(f'{MODELS}/edit', capabilities(model_id, self.row_version(model_id), outputLimit=4096, reasoningEfforts=['low', 'high']))

    def display_name(self, identifier):
        return f'Synthetic {self.label} model {identifier[-12:]}'

    def model(self, identifier, rates=RATES):
        """A model identity with recorded capabilities, a rate set in force and switched on; `rates` lists its uncached input,
        cache read, cache write and output rates as SQL, NULL where a dimension is not offered."""
        added = self.admin.call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': identifier,
                                                  'displayName': self.display_name(identifier)})
        self.edit(added['id'])
        self.query(f"INSERT INTO assistant.price_revisions VALUES('{added['id']}',1,'rates',{self.usd},{rates},NULL,NULL,NULL,NULL,NULL,'2020-01-01',NULL,"
                   f"'{self.administrator}',clock_timestamp());")
        self.admin.call(f'{MODELS}/enable', model_command(added['id'], self.row_version(added['id'])))
        return added['id']

    def create(self, name):
        return self.admin.call(f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': f'Synthetic {name} {self.stamp}'})

    def save(self, agent_state, model_id, tools=('read_current_page', 'read_market_data', 'read_trades'), days=28, **limits):
        return self.admin.call(f'{AGENTS}/save', {
            'operationId': str(uuid4()), 'agentId': agent_state['id'], 'expectedVersion': agent_state['version'],
            'description': 'Answers questions about the page it is opened on.', 'instructions': 'Quote only values you read.',
            'modelId': model_id, 'modelVersion': self.version_of(model_id), 'reasoning': 'low', 'caching': False, 'tools': list(tools),
            'allowedRoles': ['trader', 'risk_analyst'],
            'limits': {'toolSteps': 8, 'inputTokens': 200000, 'outputTokens': 2048, 'elapsedSeconds': 120, 'readSeconds': 15, 'retries': 2, 'historyTurns': 20, **limits}, 'grantDays': days})

    def publish(self, agent_state):
        return self.admin.call(f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': agent_state['id'], 'expectedVersion': agent_state['version']})
