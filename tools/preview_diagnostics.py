"""Bounded public identifiers from the owned preview's private application log."""
import re

ERROR_TYPES=frozenset(('InvalidOperationException','KeyNotFoundException','ArgumentException',
    'ArgumentOutOfRangeException','IndexOutOfRangeException','NullReferenceException','FormatException',
    'OverflowException','JsonException','NpgsqlException','PostgresException','InvalidCastException',
    'NotSupportedException','ObjectDisposedException','TimeoutException','IOException'))
SQL_STATES=frozenset(('23505','23503','23514','42501','55000','P0001','42702','42703','42P01','42883',
    '22023','22P02','22003','22007','57014','55P03','40P01','40001','XX000','08006','08001','53300',
    '53100','53200','57P01'))
CONSOLE_PREFIX=re.compile(r'(?:warn|fail): [A-Za-z0-9_.+<>`]+\[[0-9]{1,6}\] ')
CODE=re.compile(r'OpenEtrm\.(?:Api|MarketData|Rates|Shared)\.[A-Za-z_][A-Za-z0-9_.+<>`]{0,200}')


def failure_identifiers(lines):
    latest={}
    for raw in lines:
        line=raw.strip()
        prefix=CONSOLE_PREFIX.match(line)
        if prefix is not None:
            line=line[prefix.end():]
        token=None
        for prefix,kind,allowed in (
            ('Preview operation failed: ','error_type',ERROR_TYPES),
            ('Database operation rejected: ','sqlstate',SQL_STATES),
        ):
            if line.startswith(prefix) and line[len(prefix):] in allowed:
                token=kind+'='+line[len(prefix):]
        prefix='Preview failure code: '
        if line.startswith(prefix) and CODE.fullmatch(line[len(prefix):]):
            token='code='+line[len(prefix):]
        if token is not None:
            latest.pop(token,None)
            latest[token]=None
            if len(latest)>16:
                del latest[next(iter(latest))]
    return tuple(latest)


def print_failure_identifiers(path):
    try:
        with path.open(encoding='utf-8',errors='replace') as source:
            identifiers=failure_identifiers(source)
    except OSError:
        return
    for identifier in identifiers:
        print('Preview diagnostic: '+identifier,flush=True)
