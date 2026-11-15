"""Separate books for browser settlement cases in the owned synthetic database."""
from uuid import UUID, uuid4


def prepare(query, manifest):
    catalog = str(UUID(manifest['catalogId']))
    books = {name: str(uuid4()) for name in (
        'settlement_risk_browser', 'shared_side_lifecycle_browser', 'netting_risk_browser')}
    for name, book in books.items():
        query(f"INSERT INTO trading.books VALUES('{catalog}','{book}',"
              f"'d0000000-0000-0000-0000-000000000001','Synthetic {name}',1,'2020-01-01T00:00:00Z');")
    return books
