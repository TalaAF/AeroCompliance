"""One-command development smoke check for the Phase 4 vertical slice."""

from __future__ import annotations

import json
import os
import ssl
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone


BASE_URL = os.getenv("AEROCOMPLIANCE_BASE_URL", "https://localhost:7096").rstrip("/")
SSL_CONTEXT = ssl._create_unverified_context()
TOKEN: str | None = None


def request(method: str, path: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    if TOKEN:
        headers["Authorization"] = f"Bearer {TOKEN}"
    req = urllib.request.Request(BASE_URL + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, context=SSL_CONTEXT) as response:
            payload = response.read()
            return json.loads(payload) if payload else {}
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"{method} {path} -> {error.code}: {error.read().decode()}") from error


def upload(flight_id: str, scenario: str = "road_encroachment") -> tuple[int, dict]:
    boundary = f"----aerocompliance-{uuid.uuid4().hex}"
    png = __import__("base64").b64decode(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
    )
    fields = {
        "capturedAt": datetime.now(timezone.utc).isoformat(),
        "latitude": "31.9",
        "longitude": "35.2",
        "mockScenario": scenario,
    }
    chunks: list[bytes] = []
    for name, value in fields.items():
        chunks.append(
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
        )
    chunks.append(
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"phase4.png\"\r\n"
        "Content-Type: image/png\r\n\r\n".encode()
        + png
        + b"\r\n"
    )
    chunks.append(f"--{boundary}--\r\n".encode())
    req = urllib.request.Request(
        f"{BASE_URL}/api/flights/{flight_id}/images",
        data=b"".join(chunks),
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, context=SSL_CONTEXT) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"upload -> {error.code}: {error.read().decode()}") from error


def main() -> None:
    global TOKEN
    TOKEN = request("POST", "/api/auth/login", {
        "email": "admin@aerocompliance.test", "password": "ChangeMe123!"
    })["accessToken"]
    municipality_id = request("GET", "/api/municipalities?page=1&pageSize=100")["items"][0]["id"]
    users = request("GET", "/api/users?page=1&pageSize=100")["items"]
    operator_id = next(user["id"] for user in users if user["role"]["name"] == "DroneOperator")
    suffix = int(time.time())
    geometry = {"type": "MultiPolygon", "coordinates": [[[[35.1994, 31.8994], [35.2006, 31.8994],
        [35.2006, 31.9006], [35.1994, 31.9006], [35.1994, 31.8994]]]]}
    zone = request("POST", "/api/zones", {"municipalityId": municipality_id, "zoneNumber": f"P4-{suffix}",
        "name": "Phase 4 Zone", "zoneType": "residential", "geometry": geometry})
    block = request("POST", "/api/blocks", {"zoneId": zone["id"], "blockNumber": f"B-{suffix}", "geometry": geometry})
    request("POST", "/api/parcels", {"blockId": block["id"], "parcelNumber": f"P-{suffix}",
        "ownerName": "Phase Four", "zoneType": "residential", "areaM2": 12000,
        "geometry": geometry, "source": "smoke_check"})
    road_geometry = {"type": "MultiPolygon", "coordinates": [[[[35.19996, 31.89996], [35.20004, 31.89996],
        [35.20004, 31.90004], [35.19996, 31.90004], [35.19996, 31.89996]]]]}
    request("POST", "/api/roads", {"municipalityId": municipality_id, "roadCode": f"R-{suffix}",
        "name": "Phase 4 Road", "roadType": "local", "widthM": 8, "isSensitive": True,
        "geometry": road_geometry})
    flight = request("POST", "/api/flights", {"municipalityId": municipality_id,
        "droneOperatorId": operator_id, "missionName": f"Phase 4 integration {suffix}",
        "plannedArea": geometry, "scheduledAt": datetime.now(timezone.utc).isoformat(), "qualityFlag": True})
    upload_status, receipt = upload(flight["id"])
    status = {}
    for _ in range(40):
        time.sleep(0.5)
        status = request("GET", f"/api/images/{receipt['imageId']}/processing-status")
        if status["status"] in {"Completed", "AnalysisFailed", "ComplianceFailed"}:
            break
    detections = request("GET", f"/api/detections?imageId={receipt['imageId']}")
    violations = request("GET", f"/api/violations?flightId={flight['id']}")
    if status.get("status") != "Completed" or detections["totalCount"] < 1 or violations["totalCount"] < 1:
        raise RuntimeError(f"pipeline did not complete: {status}, detections={detections['totalCount']}, violations={violations['totalCount']}")
    confirmed = request("POST", f"/api/violations/{violations['items'][0]['id']}/confirm",
        {"notes": "Phase 4 end-to-end verification"})
    failure_http, failed_receipt = upload(flight["id"], "failure")
    failed_status = {}
    for _ in range(40):
        time.sleep(0.5)
        failed_status = request("GET", f"/api/images/{failed_receipt['imageId']}/processing-status")
        if failed_status["status"] == "AnalysisFailed":
            break
    if failed_status.get("status") != "AnalysisFailed":
        raise RuntimeError(f"failure path did not terminate as expected: {failed_status}")
    request("POST", f"/api/images/{failed_receipt['imageId']}/retry-processing")
    retry_status = {}
    for _ in range(40):
        time.sleep(0.5)
        retry_status = request("GET", f"/api/images/{failed_receipt['imageId']}/processing-status")
        if retry_status["status"] in {"Completed", "AnalysisFailed", "ComplianceFailed"}:
            break
    # A retry re-runs the configured behavior; a deterministic failure is allowed to fail again.
    # What matters here is that a new attempt was queued and reached an honest terminal state.
    if retry_status.get("status") not in {"Completed", "AnalysisFailed", "ComplianceFailed"} or retry_status.get("attemptCount") != 2:
        raise RuntimeError(f"retry path did not complete: {retry_status}")
    dashboard = request("GET", "/api/dashboard/summary")
    audit = request("GET", f"/api/audit?page=1&pageSize=100&entityId={receipt['imageId']}")
    print(json.dumps({"flightId": flight["id"], "imageId": receipt["imageId"], "uploadHttp": upload_status,
        "finalStatus": status["status"], "attempts": status["attemptCount"],
        "detections": detections["totalCount"], "violations": violations["totalCount"],
        "violationStatus": confirmed["status"], "completedImages": dashboard["completedProcessingCount"],
        "imageAuditEntries": audit["totalCount"], "failureUploadHttp": failure_http,
        "failureStatus": failed_status["status"], "retryStatus": retry_status["status"],
        "retryAttempts": retry_status["attemptCount"]}, separators=(",", ":")))


if __name__ == "__main__":
    main()
