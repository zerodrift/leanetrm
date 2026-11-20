"""Request coordinate transport for synthetic native integration clients.

Like the HTTP client's read-reference metadata, this belongs to the test client,
not the database. Reusing an explicit correlation supplies its original complete
coordinates under the next request's current authority. No context is saved.
"""
import json
from datetime import datetime,timezone
import os
from pathlib import Path
import re
from threading import RLock
from uuid import uuid4


class NativeReadTransport:
    marker='OPENETRM_SYNTHETIC_READ_INPUTS:'
    def __init__(self,inputs_file=None):self.sources={};self.lock=RLock();self.inputs_file=Path(inputs_file) if inputs_file else None

    def prepare(self,sql):
        binding=re.search(r'(SELECT|PERFORM)\s+security\.use_context\([^;]+\);',sql,re.I)
        creation=re.search(r'\bCREATE\s+(OR\s+REPLACE\s+)?FUNCTION\b',sql,re.I)
        # Never rewrite a function body. A request may create its own temporary
        # probe after binding; its earlier outer binding still carries inputs.
        if creation and (not binding or creation.start()<binding.start()):return sql
        worker=re.search(r'SELECT\s+risk\.bind\([^;]+\);',sql,re.I)
        if not binding and not worker:return sql
        commit=sql.rfind('COMMIT;')
        rollback=sql.rfind('ROLLBACK;')
        boundary=max(commit,rollback)
        if boundary<0:return sql
        if binding:
            invoke='PERFORM' if binding[1].upper()=='PERFORM' else 'SELECT'
            with self.lock:sources=[s for identifier,s in self.sources.items() if identifier in sql]
            registrations=''.join(f"{invoke} security.register_read_source('{s['catalog_id']}','{s['id']}','{s['effective_on']}','{s['known_at']}',{s['schedule_prefix']},'{s['system_id']}',{s['timeline']});" for s in sources)
            sql=sql[:binding.end()]+registrations+sql[binding.end():]
            commit=sql.rfind('COMMIT;')
            rollback=sql.rfind('ROLLBACK;')
            boundary=max(commit,rollback)
        # Reads may already finish explicitly. In that case their producer owns
        # any transported values; there are no remaining coordinates to collect.
        collection=f"SELECT '{self.marker}'||coalesce(jsonb_agg(to_jsonb(s)),'[]'::jsonb)::text FROM security.operation_read_inputs s;"
        release='SELECT security.finish_operation_read();' if commit>rollback else ''
        return sql[:boundary]+collection+release+sql[boundary:]

    def receive(self,output):
        with self.lock:return self._receive(output)

    def _receive(self,output):
        kept=[];changed=False
        for line in output.splitlines():
            if line.startswith(self.marker):
                for source in json.loads(line[len(self.marker):]):
                    instant=datetime.fromisoformat(source['known_at'])
                    if instant.tzinfo is None:raise AssertionError('A native read omitted its knowledge time zone')
                    source['known_at']=instant.astimezone(timezone.utc).isoformat(timespec='microseconds')
                    identifier=source['id']
                    previous=self.sources.get(identifier)
                    if previous:
                        previous['known_at']=datetime.fromisoformat(previous['known_at']).astimezone(timezone.utc).isoformat(timespec='microseconds')
                    coordinates=('catalog_id','effective_on','known_at','schedule_prefix','system_id','timeline')
                    if previous and any(previous[k]!=source[k] for k in coordinates):raise AssertionError('A native read changed its exact coordinates')
                    changed|=previous is None
                    self.sources[identifier]=source
            else:kept.append(line)
        if changed and self.inputs_file:
            staged=self.inputs_file.with_name('.'+self.inputs_file.name+'.'+uuid4().hex)
            try:
                with os.fdopen(os.open(staged,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600),'w') as stream:
                    json.dump(self.sources,stream)
                os.replace(staged,self.inputs_file)
            finally:staged.unlink(missing_ok=True)
        return '\n'.join(kept).strip()


def captured_input(query,identifier):
    """An input value captured by this synthetic client, never a database lookup."""
    return query.read_sources[identifier]


def register_captured_input(query,identifier):
    """Supply an observed full input tuple to an interactive native operation."""
    source=captured_input(query,identifier)
    return (f"SELECT security.register_read_source('{source['catalog_id']}','{identifier}','{source['effective_on']}',"
            f"'{source['known_at']}',{source['schedule_prefix']},'{source['system_id']}',{source['timeline']});")


def native_query(raw_query,inputs_file=None):
    """Wrap one disposable native client's request/response transport."""
    transport=NativeReadTransport(inputs_file)
    def query(sql):return transport.receive(raw_query(transport.prepare(sql)))
    query.raw=raw_query
    query.read_sources=transport.sources
    return query


def captured_context(query,identifier):
    source=captured_input(query,identifier)
    return dict(contextId=identifier,effectiveOn=source['effective_on'],knownAt=source['known_at'],
                knownThrough=source['schedule_prefix'])


def select_context_inputs(catalog,context):
    """Explicit native equivalent of the HTTP date/cutoff selection operation."""
    return (f"DO $inputs$BEGIN IF NOT EXISTS(SELECT 1 FROM security.resolve_read_cut('{catalog}',"
            f"'{context['effectiveOn']}','{context['knownAt']}',false) c WHERE c.id='{context['contextId']}'::uuid "
            f"AND c.known_through={int(context['knownThrough'])}) THEN RAISE EXCEPTION 'Synthetic input coordinates changed';"
            "END IF;END$inputs$;")


def select_owned_inputs(catalog,source):
    """Reconstruct a synthetic business owner's exact coordinates and lineage."""
    return (f"DO $inputs$DECLARE resolved uuid;BEGIN resolved=security.resolve_operation_context('{catalog}','{source['effective_on']}',"
            f"'{source['known_at']}');IF resolved IS DISTINCT FROM '{source['id']}'::uuid THEN RAISE EXCEPTION 'Synthetic owned correlation changed';END IF;"
            "IF NOT EXISTS(SELECT 1 FROM security.operation_read_inputs "
            f"WHERE catalog_id='{catalog}' AND id='{source['id']}' AND system_id='{source['system_id']}' AND timeline={source['timeline']}) "
            "THEN RAISE EXCEPTION 'Synthetic owned input coordinates changed';END IF;END$inputs$;")
