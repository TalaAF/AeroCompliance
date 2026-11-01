"""PostgreSQL claim and durable result for a stable AI request ID."""

import hashlib
import json
import threading
import uuid
from dataclasses import dataclass

import psycopg


LEASE_SECONDS = 120
RENEW_SECONDS = 20


class JobCollision(Exception):
    """The same request ID arrived with different request bytes."""


class ClaimLost(Exception):
    """Another worker reclaimed the lease before this worker could persist its result."""


@dataclass(frozen=True)
class Claim:
    state: str
    token: uuid.UUID | None = None
    routing_key: str | None = None
    payload: dict | None = None


def connection_kwargs(connection_string: str) -> dict:
    # The AppHost, Compose and single-container entrypoint already pass this Npgsql-style string.
    values = dict(part.strip().split("=", 1) for part in connection_string.split(";") if part.strip())
    lowered = {key.lower(): value for key, value in values.items()}
    return {
        "host": lowered["host"],
        "port": int(lowered.get("port", "5432")),
        "dbname": lowered["database"],
        "user": lowered["username"],
        "password": lowered["password"],
        "connect_timeout": 10,
    }


class JobStore:
    def __init__(self, connection_string: str):
        self.connect_options = connection_kwargs(connection_string)

    def connect(self):
        return psycopg.connect(**self.connect_options)

    def claim(self, request_id: str, body: bytes) -> Claim:
        job_id = uuid.UUID(request_id)
        request_hash = hashlib.sha256(body).hexdigest()
        with self.connect() as conn, conn.cursor() as cursor:
            cursor.execute("""
                INSERT INTO ai_processing_jobs (request_id, request_hash, state, attempt_count)
                VALUES (%s, %s, 'processing', 0)
                ON CONFLICT (request_id) DO NOTHING
                """, (job_id, request_hash))
            cursor.execute("""
                SELECT request_hash, state, lease_until > now(), result_routing_key, result_payload
                FROM ai_processing_jobs WHERE request_id = %s FOR UPDATE
                """, (job_id,))
            stored_hash, state, lease_is_live, routing_key, payload = cursor.fetchone()
            if stored_hash != request_hash:
                raise JobCollision(f"Request ID {job_id} was reused with different content")
            if state == "result_ready":
                if not routing_key or not payload:
                    raise RuntimeError("Stored AI result is incomplete")
                return Claim("result_ready", routing_key=routing_key, payload=payload)
            if lease_is_live:
                return Claim("busy")
            token = uuid.uuid4()
            cursor.execute("""
                UPDATE ai_processing_jobs
                SET lease_token = %s, lease_until = now() + (%s * interval '1 second'),
                    attempt_count = attempt_count + 1, updated_at = now()
                WHERE request_id = %s
                """, (token, LEASE_SECONDS, job_id))
            return Claim("claimed", token=token)

    def renew(self, request_id: str, token: uuid.UUID) -> bool:
        with self.connect() as conn, conn.cursor() as cursor:
            cursor.execute("""
                UPDATE ai_processing_jobs
                SET lease_until = now() + (%s * interval '1 second'), updated_at = now()
                WHERE request_id = %s AND lease_token = %s AND state = 'processing'
                """, (LEASE_SECONDS, uuid.UUID(request_id), token))
            return cursor.rowcount == 1

    def store_result(self, request_id: str, token: uuid.UUID, routing_key: str, payload: dict):
        serialized = json.dumps(payload, separators=(",", ":"))
        with self.connect() as conn, conn.cursor() as cursor:
            cursor.execute("""
                UPDATE ai_processing_jobs
                SET state = 'result_ready', result_routing_key = %s,
                    result_payload = %s::jsonb, lease_token = NULL, lease_until = NULL,
                    updated_at = now()
                WHERE request_id = %s AND lease_token = %s AND state = 'processing'
                """, (routing_key, serialized, uuid.UUID(request_id), token))
            if cursor.rowcount != 1:
                raise ClaimLost(request_id)

    def release(self, request_id: str, token: uuid.UUID):
        with self.connect() as conn, conn.cursor() as cursor:
            cursor.execute("""
                UPDATE ai_processing_jobs SET lease_token = NULL, lease_until = NULL, updated_at = now()
                WHERE request_id = %s AND lease_token = %s AND state = 'processing'
                """, (uuid.UUID(request_id), token))


class LeaseRenewer:
    def __init__(self, store: JobStore, request_id: str, token: uuid.UUID):
        self.store, self.request_id, self.token = store, request_id, token
        self.stop_signal = threading.Event()
        self.lost = False
        self.thread = threading.Thread(target=self.run, daemon=True, name="aerocompliance-job-lease")

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop_signal.set()
        self.thread.join()

    def run(self):
        while not self.stop_signal.wait(RENEW_SECONDS):
            try:
                if not self.store.renew(self.request_id, self.token):
                    self.lost = True
                    return
            except Exception as exc:
                # A transient database outage can recover before the lease expires. The final
                # conditional store_result still fences a worker that was actually displaced.
                print(f"AI job lease renewal failed: {type(exc).__name__}", flush=True)
