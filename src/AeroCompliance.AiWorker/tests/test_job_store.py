import hashlib
import json
import os
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from types import SimpleNamespace
from unittest.mock import patch

os.environ.setdefault("ConnectionStrings__messaging", "amqp://guest:guest@127.0.0.1:5672")
os.environ.setdefault("ConnectionStrings__aerocompliancedb", "Host=127.0.0.1;Database=test;Username=test;Password=test")
os.environ.setdefault("MINIO_ENDPOINT", "127.0.0.1:9000")
os.environ.setdefault("MINIO_ACCESS_KEY", "test")
os.environ.setdefault("MINIO_SECRET_KEY", "test")

from aerocompliance_ai.delivery import COMPLETED_KEY, process_delivery  # noqa: E402
from aerocompliance_ai.job_store import ClaimLost, JobStore  # noqa: E402


class JobStoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        connection = os.getenv("AI_TEST_DATABASE_CONNECTION")
        if not connection:
            raise unittest.SkipTest("AI_TEST_DATABASE_CONNECTION is required for real PostgreSQL tests")
        cls.store = JobStore(connection)
        with cls.store.connect() as db:
            db.execute("""
                CREATE TABLE IF NOT EXISTS ai_processing_jobs (
                    request_id uuid PRIMARY KEY, request_hash varchar(64) NOT NULL,
                    state varchar(20) NOT NULL, lease_token uuid, lease_until timestamptz,
                    result_routing_key varchar(150), result_payload jsonb,
                    attempt_count integer NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now())
                """)

    def request(self):
        request_id = str(uuid.uuid4())
        request = {"eventId": str(uuid.uuid4()), "requestId": request_id,
                   "imageId": str(uuid.uuid4()), "bucket": "tests", "objectKey": "image.png",
                   "sha256": hashlib.sha256(b"image").hexdigest(), "correlationId": str(uuid.uuid4())}
        return request, json.dumps(request).encode()

    def test_duplicate_claim_and_completed_result_reuse(self):
        request, body = self.request()
        first = self.store.claim(request["requestId"], body)
        second = self.store.claim(request["requestId"], body)
        self.assertEqual("claimed", first.state)
        self.assertEqual("busy", second.state)
        payload = {"eventId": str(uuid.uuid4()), "requestId": request["requestId"]}
        self.store.store_result(request["requestId"], first.token, COMPLETED_KEY, payload)
        replay = self.store.claim(request["requestId"], body)
        self.assertEqual("result_ready", replay.state)
        self.assertEqual(payload, replay.payload)

    def test_simultaneous_workers_only_allow_one_inference_claim(self):
        request, body = self.request()
        with ThreadPoolExecutor(max_workers=2) as workers:
            claims = list(workers.map(
                lambda _: self.store.claim(request["requestId"], body), range(2)))
        self.assertEqual(["busy", "claimed"], sorted(claim.state for claim in claims))

    def test_expired_claim_is_recovered_and_old_worker_is_fenced(self):
        request, body = self.request()
        original = self.store.claim(request["requestId"], body)
        with self.store.connect() as db:
            db.execute("UPDATE ai_processing_jobs SET lease_until = now() - interval '1 second' "
                       "WHERE request_id = %s", (uuid.UUID(request["requestId"]),))
        recovered = self.store.claim(request["requestId"], body)
        self.assertEqual("claimed", recovered.state)
        self.assertNotEqual(original.token, recovered.token)
        with self.assertRaises(ClaimLost):
            self.store.store_result(request["requestId"], original.token, COMPLETED_KEY, {"eventId": "old"})
        self.store.store_result(request["requestId"], recovered.token, COMPLETED_KEY, {"eventId": "new"})
        self.assertEqual("new", self.store.claim(request["requestId"], body).payload["eventId"])

    def test_publish_failure_replays_durable_result_without_new_inference(self):
        request, body = self.request()
        published, acknowledgements = [], []
        worker = SimpleNamespace(
            jobs=self.store,
            s=SimpleNamespace(mode="mock", model_name="test", model_version="1", threshold=0.5),
            finish_delivery=lambda connection, channel, tag, ack, requeue=False:
                acknowledgements.append((ack, requeue)))

        def publish(key, payload):
            published.append(key)
            if key == COMPLETED_KEY and published.count(COMPLETED_KEY) == 1:
                raise RuntimeError("simulated publish failure")

        worker.publish = publish
        result = {"eventId": str(uuid.uuid4()), "requestId": request["requestId"],
                  "completedAt": datetime.now(timezone.utc).isoformat(), "detections": []}
        with patch("aerocompliance_ai.delivery.analyze_request", return_value=(COMPLETED_KEY, result)) as analyze:
            process_delivery(worker, None, None, 1, body)
            process_delivery(worker, None, None, 2, body)
        self.assertEqual(1, analyze.call_count)
        self.assertEqual([(False, True), (True, False)], acknowledgements)
        self.assertEqual(2, published.count(COMPLETED_KEY))

    def test_saved_result_publish_failure_waits_before_requeue(self):
        request, body = self.request()
        claim = self.store.claim(request["requestId"], body)
        payload = {"eventId": str(uuid.uuid4()), "requestId": request["requestId"]}
        self.store.store_result(request["requestId"], claim.token, COMPLETED_KEY, payload)
        acknowledgements = []
        worker = SimpleNamespace(
            jobs=self.store,
            publish=lambda key, value: (_ for _ in ()).throw(RuntimeError("route unavailable")),
            finish_delivery=lambda connection, channel, tag, ack, requeue=False:
                acknowledgements.append((ack, requeue)))
        with patch("aerocompliance_ai.delivery.time.sleep") as pause:
            process_delivery(worker, None, None, 1, body)
        pause.assert_called_once_with(2)
        self.assertEqual([(False, True)], acknowledgements)
        self.assertEqual("result_ready", self.store.claim(request["requestId"], body).state)


if __name__ == "__main__":
    unittest.main()
