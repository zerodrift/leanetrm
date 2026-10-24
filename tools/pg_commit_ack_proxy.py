"""PostgreSQL wire fault injection for owned disposable tests only.

The proxy forwards plaintext Unix-domain PostgreSQL traffic. It suppresses one
runtime COMMIT CommandComplete only after an independent query confirms the
expected report and terminal state committed. No production hook is required.
"""
from pathlib import Path
import json
import socket
import struct
import threading
import time


class CommitAckProxy:
    def __init__(self, upstream_socket, directory, committed, runtime_user='pilot_fixture_runtime', port=55439):
        self.upstream = str(upstream_socket)
        self.directory = Path(directory)
        self.path = self.directory / f'.s.PGSQL.{port}'
        self.committed = committed
        self.runtime_user = runtime_user
        self.dropped = threading.Event()
        self.stopping = threading.Event()
        self.lock = threading.Lock()
        self.connections = set()
        self.events = []
        self.errors = []
        self.listener = None
        self.thread = None

    @staticmethod
    def read_exact(connection, length):
        chunks = bytearray()
        while len(chunks) < length:
            chunk = connection.recv(length - len(chunks))
            if not chunk:
                raise EOFError()
            chunks.extend(chunk)
        return bytes(chunks)

    def __enter__(self):
        self.directory.mkdir(mode=0o700, parents=True, exist_ok=False)
        self.listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.listener.bind(str(self.path)); self.listener.listen(); self.listener.settimeout(.2)
        self.thread = threading.Thread(target=self.accept, daemon=True)
        self.thread.start()
        return self

    def accept(self):
        while not self.stopping.is_set():
            try:
                client, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            thread = threading.Thread(target=self.connection, args=(client,), daemon=True)
            thread.start()

    def connection(self, client):
        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        with self.lock:
            self.connections.update((client, server))
        try:
            server.connect(self.upstream)
            header = self.read_exact(client, 4)
            size = struct.unpack('!I', header)[0]
            if not 8 <= size <= 100000:
                raise ValueError(f'Invalid PostgreSQL startup length {size}')
            startup = self.read_exact(client, size - 4)
            protocol = struct.unpack('!I', startup[:4])[0]
            user = None
            if protocol == 196608:
                fields = startup[4:].rstrip(b'\0').split(b'\0')
                parameters = dict(zip(fields[::2], fields[1::2]))
                user = parameters.get(b'user', b'').decode()
            elif protocol != 80877102:
                raise ValueError('Test proxy requires SSL Mode=Disable and PostgreSQL v3 or cancellation startup')
            server.sendall(header + startup)
            forwarding = threading.Thread(target=self.forward_client, args=(client, server), daemon=True)
            forwarding.start()
            while not self.stopping.is_set():
                message_type = self.read_exact(server, 1)
                length_bytes = self.read_exact(server, 4)
                length = struct.unpack('!I', length_bytes)[0]
                if length < 4:
                    raise ValueError('Invalid backend message length')
                payload = self.read_exact(server, length - 4)
                if message_type == b'C' and payload == b'COMMIT\0' and user == self.runtime_user:
                    with self.lock:
                        if not self.dropped.is_set():
                            evidence = self.committed()
                            if evidence:
                                self.events.append({'event': 'commit_ack_dropped', 'monotonic': time.monotonic(), 'user': user, 'durableEvidence': evidence})
                                self.dropped.set()
                                return  # Neither COMMIT nor subsequent ReadyForQuery reaches Npgsql.
                client.sendall(message_type + length_bytes + payload)
        except (EOFError, ConnectionError, OSError):
            pass
        except BaseException as error:
            with self.lock:
                self.errors.append(f'{type(error).__name__}: {error}')
        finally:
            for connection in (client, server):
                try: connection.shutdown(socket.SHUT_RDWR)
                except OSError: pass
                connection.close()
            with self.lock:
                self.connections.difference_update((client, server))

    @staticmethod
    def forward_client(client, server):
        try:
            while True:
                payload = client.recv(65536)
                if not payload: break
                server.sendall(payload)
        except (ConnectionError, OSError):
            pass
        finally:
            try: server.shutdown(socket.SHUT_WR)
            except OSError: pass

    def __exit__(self, *_):
        self.stopping.set()
        self.listener.close()
        with self.lock:
            pending = list(self.connections)
        for connection in pending:
            try: connection.shutdown(socket.SHUT_RDWR)
            except OSError: pass
            connection.close()
        self.thread.join(timeout=3)
        self.path.unlink(missing_ok=True)
        self.directory.rmdir()
