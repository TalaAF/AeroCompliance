"""Durable AI request handling; the Pika consumer thread only schedules this work."""

import hashlib
import json
import os
import tempfile
import time
import traceback
import uuid
from datetime import datetime, timezone

from .job_store import ClaimLost, JobCollision, LeaseRenewer
from .model import ModelFailure

EXCHANGE = "aerocompliance.processing"
REQUEST_KEY = "image.processing.requested.v1"
STARTED_KEY = "image.analysis.started.v1"
COMPLETED_KEY = "image.analysis.completed.v1"
FAILED_KEY = "image.analysis.failed.v1"
QUEUE = "aerocompliance.ai.image-processing.v1"
DLQ = "aerocompliance.ai.image-processing.dlq.v1"


def utcnow():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def process_delivery(worker, connection, channel, delivery_tag, body):
    try:
        request = json.loads(body)
        required = ("eventId", "requestId", "imageId", "bucket", "objectKey", "sha256", "correlationId")
        if any(not request.get(key) for key in required):
            raise ValueError("Required request fields are missing")
        uuid.UUID(request["requestId"])
        claim = worker.jobs.claim(request["requestId"], body)
    except (ValueError, KeyError, TypeError, JobCollision) as exc:
        print(f"invalid AI processing request: {type(exc).__name__}", flush=True)
        worker.finish_delivery(connection, channel, delivery_tag, False, False)
        return
    except Exception as exc:
        print(f"AI job claim unavailable: {type(exc).__name__}", flush=True)
        worker.finish_delivery(connection, channel, delivery_tag, False, True)
        return

    if claim.state == "busy":
        # A second worker must not infer while the first lease is live. The message remains
        # unacknowledged and is requeued after a short pause; a crashed owner can be reclaimed.
        time.sleep(2)
        worker.finish_delivery(connection, channel, delivery_tag, False, True)
        return
    if claim.state == "result_ready":
        try:
            worker.publish(claim.routing_key, claim.payload)
            worker.finish_delivery(connection, channel, delivery_tag, True)
        except Exception as exc:
            print(f"stored AI result publish failed: {type(exc).__name__}", flush=True)
            time.sleep(2)
            worker.finish_delivery(connection, channel, delivery_tag, False, True)
        return

    started_at, started_clock = utcnow(), time.monotonic()
    base = {"schemaVersion": 1, "correlationId": request["correlationId"],
            "causationId": request["eventId"], "occurredAt": started_at,
            "requestId": request["requestId"], "imageId": request["imageId"], "attempt": 1}
    started = {**base, "eventId": str(uuid.uuid5(uuid.UUID(request["requestId"]), "started")),
               "mode": worker.s.mode, "modelName": worker.s.model_name,
               "modelVersion": worker.s.model_version, "confidenceThreshold": worker.s.threshold,
               "startedAt": started_at}
    try:
        worker.publish(STARTED_KEY, started)
    except Exception as exc:
        print(f"AI started event publish failed: {type(exc).__name__}", flush=True)
        try:
            worker.jobs.release(request["requestId"], claim.token)
        except Exception:
            pass  # The claim still expires if PostgreSQL is unavailable.
        worker.finish_delivery(connection, channel, delivery_tag, False, True)
        return

    try:
        with LeaseRenewer(worker.jobs, request["requestId"], claim.token) as renewal:
            try:
                routing_key, payload = analyze_request(worker, request, base, started_at, started_clock)
            except Exception as exc:
                code = exc.code if isinstance(exc, ModelFailure) else "AI_WORKER_ERROR"
                message = str(exc)[:500] if isinstance(exc, ModelFailure) else "AI processing failed safely."
                if not isinstance(exc, ModelFailure):
                    print(f"unhandled analysis error for image {request['imageId']}: "
                          f"{type(exc).__name__}: {exc}", flush=True)
                    traceback.print_exc()
                failed_at = utcnow()
                routing_key = FAILED_KEY
                payload = {**base, "eventId": str(uuid.uuid5(uuid.UUID(request["requestId"]), "failed")),
                           "occurredAt": failed_at, "stage": "analysis", "errorCode": code,
                           "message": message,
                           "retryable": code not in ("MALFORMED_REQUEST", "MISSING_CAPTURE_COORDINATES"),
                           "failedAt": failed_at}
            if renewal.lost:
                raise ClaimLost(request["requestId"])
            # Persist the complete result before publishing. A redelivery can republish this
            # exact event without repeating inference after a publish or ACK failure.
            worker.jobs.store_result(request["requestId"], claim.token, routing_key, payload)
        worker.publish(routing_key, payload)
        worker.finish_delivery(connection, channel, delivery_tag, True)
    except Exception as exc:
        print(f"AI result will retry from durable job state: {type(exc).__name__}", flush=True)
        time.sleep(2)
        worker.finish_delivery(connection, channel, delivery_tag, False, True)


def analyze_request(worker, request, base, started_at, started_clock):
    # Ultralytics resolves format by suffix even when the raster bytes are valid.
    suffix = os.path.splitext(request.get("objectKey") or "")[1].lower()
    if suffix not in (".tif", ".tiff", ".png", ".jpg", ".jpeg"):
        suffix = {"image/tiff": ".tif", "image/geotiff": ".tif", "image/png": ".png",
                  "image/jpeg": ".jpg"}.get((request.get("contentType") or "").lower(), ".tif")
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as temp:
        path = temp.name
    try:
        worker.minio.fget_object(request["bucket"], request["objectKey"], path)
        digest = hashlib.sha256()
        with open(path, "rb") as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(chunk)
        if digest.hexdigest().lower() != request["sha256"].lower():
            raise ModelFailure("HASH_MISMATCH", "Stored object hash does not match the processing request.")
        raster = worker._extract_raster_metadata(request, path)
        outcome = worker.runner.analyze(request, path)
        completed_at = utcnow()
        payload = {**base, "eventId": str(uuid.uuid5(uuid.UUID(request["requestId"]), "completed")),
                   "occurredAt": completed_at, "mode": worker.s.mode,
                   "modelName": worker.s.model_name, "modelVersion": worker.s.model_version,
                   "confidenceThreshold": worker.s.threshold,
                   "sourceCrs": raster.crs if raster else None, "outputCrs": "EPSG:4326",
                   "startedAt": started_at, "completedAt": completed_at,
                   "durationMs": int((time.monotonic() - started_clock) * 1000),
                   "detections": outcome.detections,
                   "rasterWidth": raster.width if raster else None,
                   "rasterHeight": raster.height if raster else None,
                   "affineTransform": json.dumps(raster.affine_transform) if raster else None,
                   "modelOnlyDetections": json.dumps(outcome.model_only) if outcome.model_only else None,
                   "modelOnlyCount": len(outcome.model_only),
                   "georeferencingPolicy": json.dumps(outcome.georeferencing) if outcome.georeferencing else None}
        return COMPLETED_KEY, payload
    finally:
        if os.path.exists(path):
            os.remove(path)
