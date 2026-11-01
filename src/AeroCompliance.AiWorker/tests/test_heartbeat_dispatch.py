import os
import unittest
from types import SimpleNamespace


os.environ.setdefault("ConnectionStrings__messaging", "amqp://guest:guest@127.0.0.1:5672")
os.environ.setdefault("MINIO_ENDPOINT", "127.0.0.1:9000")
os.environ.setdefault("MINIO_ACCESS_KEY", "test")
os.environ.setdefault("MINIO_SECRET_KEY", "test")

from aerocompliance_ai.main import Worker  # noqa: E402


class RecordingExecutor:
    def __init__(self):
        self.submission = None

    def submit(self, *args):
        self.submission = args


class HeartbeatDispatchTests(unittest.TestCase):
    def test_delivery_callback_dispatches_analysis_off_the_pika_thread(self):
        worker = Worker.__new__(Worker)
        worker.executor = RecordingExecutor()
        connection = object()
        channel = object()
        method = SimpleNamespace(delivery_tag=42)

        worker.callback(connection, channel, method, None, b"payload")

        self.assertIsNotNone(worker.executor.submission)
        _, submitted_connection, submitted_channel, delivery_tag, body = worker.executor.submission
        self.assertIs(submitted_connection, connection)
        self.assertIs(submitted_channel, channel)
        self.assertEqual(42, delivery_tag)
        self.assertEqual(b"payload", body)

    def test_connection_parameters_enable_a_finite_heartbeat(self):
        worker = Worker.__new__(Worker)
        worker.s = SimpleNamespace(
            rabbitmq_url="amqp://guest:guest@127.0.0.1:5672",
            rabbitmq_heartbeat=37,
        )

        parameters = worker.connection_parameters()

        self.assertEqual(37, parameters.heartbeat)
        self.assertEqual(120, parameters.blocked_connection_timeout)


if __name__ == "__main__":
    unittest.main()
