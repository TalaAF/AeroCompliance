"""Populate a new single-container volume through the public demo API.

The entrypoint enables this only for volumes it initialized. Every create step first
looks up its synthetic name, so a restart after partial work resumes without replacing
images, decisions, reports, credentials, or any user-authored data.
"""

import json
import os
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path


BASE = "http://127.0.0.1:8081"
MUNICIPALITY_ID = "11111111-1111-1111-1111-111111111111"
DATA_ROOT = Path(os.environ.get("AEROCOMPLIANCE_DATA_ROOT", "/var/lib/aerocompliance"))
MARKER = DATA_ROOT / "config" / "demo-scenario-v1.done"
ASSETS = Path("/opt/aerocompliance/demo-assets")
TOKEN = ""
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def request(method, path, payload=None, *, headers=None, raw=False):
    outgoing = {"Accept": "application/json"}
    if TOKEN:
        outgoing["Authorization"] = f"Bearer {TOKEN}"
    if headers:
        outgoing.update(headers)
    if isinstance(payload, (dict, list)):
        payload = json.dumps(payload).encode("utf-8")
        outgoing["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE + path, data=payload, headers=outgoing, method=method)
    try:
        with OPENER.open(req, timeout=120) as response:
            body = response.read()
    except urllib.error.HTTPError as error:
        detail = error.read(600).decode("utf-8", "replace")
        raise RuntimeError(f"{method} {path}: HTTP {error.code}: {detail}") from error
    if raw:
        return body
    return json.loads(body) if body else None


def wait_for_api():
    deadline = time.monotonic() + 600
    while time.monotonic() < deadline:
        try:
            # Nginx serves /health itself, so poll the API's own port before login.
            with OPENER.open("http://127.0.0.1:8081/health", timeout=5):
                pass
            return
        except (OSError, urllib.error.URLError, RuntimeError):
            time.sleep(2)
    raise TimeoutError("Demo API did not become ready within 10 minutes")


def login():
    global TOKEN
    password = os.environ["DEMO_PASSWORD"]
    response = request("POST", "/api/auth/login", {
        "email": "admin@aerocompliance.test", "password": password,
    })
    TOKEN = response["accessToken"]


def page(path):
    return request("GET", path)["items"]


def planned_area():
    ring = [
        [35.1994, 31.8994], [35.2006, 31.8994],
        [35.2006, 31.9006], [35.1994, 31.9006], [35.1994, 31.8994],
    ]
    return {"type": "MultiPolygon", "coordinates": [[ring]]}


def ensure_flight(name, operator_id, scheduled_at, purpose, *, complete=True):
    existing = next((f for f in page("/api/flights?pageSize=100")
                     if f["missionName"] == name), None)
    if existing is None:
        existing = request("POST", "/api/flights", {
            "municipalityId": MUNICIPALITY_ID,
            "droneOperatorId": operator_id,
            "missionName": name,
            "plannedArea": planned_area(),
            "scheduledAt": scheduled_at.isoformat(),
            "weatherConditions": json.dumps({
                "summary": "Clear skies (synthetic demo; no real flight occurred)"
            }),
            "qualityFlag": True,
        })
        print(f"Demo scenario: created flight {name}", flush=True)
    flight_id = existing["id"]

    missions = page(f"/api/flights/{flight_id}/missions?pageSize=100")
    if not any(m["purpose"] == purpose for m in missions) and existing["status"] == "planned":
        request("POST", f"/api/flights/{flight_id}/missions", {
            "purpose": purpose, "altitudeM": 65, "overlapPercent": 75,
            "notes": "SYNTHETIC DEMO mission; fictional coordinates and generated imagery.",
        })

    if complete:
        details = request("GET", f"/api/flights/{flight_id}")
        if details["status"] == "planned":
            details = request("POST", f"/api/flights/{flight_id}/status-transitions", {
                "targetStatus": "in_progress", "reason": "Synthetic demo workflow",
            })
        if details["status"] == "in_progress":
            request("POST", f"/api/flights/{flight_id}/status-transitions", {
                "targetStatus": "completed", "reason": "Synthetic demo workflow",
            })
    return flight_id


def multipart_image(path, filename, fields):
    boundary = f"aerocompliance-demo-{uuid.uuid4().hex}"
    chunks = []
    for name, value in fields.items():
        chunks.append((f"--{boundary}\r\nContent-Disposition: form-data; "
                       f'name="{name}"\r\n\r\n{value}\r\n').encode("utf-8"))
    chunks.append((f"--{boundary}\r\nContent-Disposition: form-data; "
                   f'name="file"; filename="{filename}"\r\n'
                   "Content-Type: image/png\r\n\r\n").encode("utf-8"))
    chunks.append(path.read_bytes())
    chunks.append(f"\r\n--{boundary}--\r\n".encode("utf-8"))
    return b"".join(chunks), f"multipart/form-data; boundary={boundary}"


def ensure_image(flight_id, filename, asset, scenario, longitude, latitude, captured_at):
    existing = next((i for i in page(f"/api/flights/{flight_id}/images?pageSize=100")
                     if i["originalFileName"] == filename), None)
    if existing is None:
        body, content_type = multipart_image(ASSETS / asset, filename, {
            "mockScenario": scenario,
            "longitude": str(longitude), "latitude": str(latitude),
            "capturedAt": captured_at.isoformat(),
            "altitudeM": "65", "cameraHeadingDeg": "0",
        })
        uploaded = request("POST", f"/api/flights/{flight_id}/images", body,
                           headers={"Content-Type": content_type})
        image_id = uploaded["imageId"]
        print(f"Demo scenario: uploaded {filename}", flush=True)
    else:
        image_id = existing["id"]

    deadline = time.monotonic() + 600
    while time.monotonic() < deadline:
        state = request("GET", f"/api/images/{image_id}/processing-status")
        status = state["status"].lower()
        if status == "completed":
            break
        if status in ("analysisfailed", "compliancefailed"):
            raise RuntimeError(f"Demo image {filename} processing failed: {state['errorCode']}")
        time.sleep(2)
    else:
        raise TimeoutError(f"Demo image {filename} did not finish processing")

    detections = page(f"/api/detections?imageId={image_id}&pageSize=100")
    expected_class = "road" if scenario == "road" else "building"
    if not any(d["className"] == expected_class and
               d["resultSource"] == "MockGeographicScenario" for d in detections):
        raise RuntimeError(f"Demo image {filename} has no labelled mock {expected_class} result")
    return image_id


def candidate_for_image(flight_id, filename, violation_type):
    for summary in page(f"/api/violations?flightId={flight_id}&pageSize=100"):
        if summary["violationType"] != violation_type:
            continue
        details = request("GET", f"/api/violations/{summary['id']}")
        if details["sourceImageFileName"] == filename:
            return details
    raise RuntimeError(f"No {violation_type} demo candidate for {filename}")


def seed():
    wait_for_api()
    login()
    operators = page("/api/users?role=DroneOperator&pageSize=100")
    operator = next(u for u in operators if (u.get("municipality") or {}).get("id") == MUNICIPALITY_ID)
    today = datetime.now(timezone.utc).replace(hour=9, minute=0, second=0, microsecond=0)

    street = ensure_flight("SYNTHETIC DEMO - Street and rooftop survey", operator["id"],
                           today - timedelta(days=14), "Building survey")
    parcel = ensure_flight("SYNTHETIC DEMO - Parcel edge assessment", operator["id"],
                           today - timedelta(days=7), "Parcel survey")
    road = ensure_flight("SYNTHETIC DEMO - Road corridor survey", operator["id"],
                         today - timedelta(days=3), "Road survey")

    pending_street = "SYNTHETIC-DEMO-building-street.png"
    pending_parcel = "SYNTHETIC-DEMO-parcel-extension.png"
    report_image = "SYNTHETIC-DEMO-courtyard-annex.png"
    road_image = "SYNTHETIC-DEMO-road-corridor.png"
    ensure_image(street, pending_street, "synthetic-building-street.png", "road_encroachment",
                 35.20000, 31.90000, today - timedelta(days=14))
    ensure_image(parcel, pending_parcel, "synthetic-parcel-extension.png", "parcel_encroachment",
                 35.20057, 31.90010, today - timedelta(days=7))
    ensure_image(street, report_image, "synthetic-courtyard-annex.png", "road_encroachment",
                 35.20000, 31.90024, today - timedelta(days=14))
    ensure_image(road, road_image, "synthetic-road-corridor.png", "road",
                 35.20000, 31.90040, today - timedelta(days=3))

    street_candidate = candidate_for_image(street, pending_street, "road_encroachment")
    parcel_candidate = candidate_for_image(parcel, pending_parcel, "parcel_encroachment")
    report_candidate = candidate_for_image(street, report_image, "road_encroachment")
    if street_candidate["status"] != "under_review" or parcel_candidate["status"] != "under_review":
        raise RuntimeError("A pending demo candidate was unexpectedly reviewed during initialization")

    if report_candidate["status"] == "under_review":
        request("POST", f"/api/violations/{report_candidate['id']}/confirm", {
            "notes": "SYNTHETIC DEMO: automated sample review solely to show the report workflow. "
                     "Fictional coordinates and mock detection; not enforcement evidence.",
        })
    elif report_candidate["status"] != "confirmed_under_notification":
        raise RuntimeError("The sample report candidate has an unexpected review status")
    report = request("POST", f"/api/violations/{report_candidate['id']}/reports", headers={
        "X-AeroCompliance-Demo-Seed": "1",
    })
    pdf = request("GET", f"/api/reports/{report['id']}/download", raw=True)
    if not pdf.startswith(b"%PDF-") or len(pdf) < 1000:
        raise RuntimeError("The generated sample report is not a usable PDF")

    summary = {
        "version": 1,
        "completedAt": datetime.now(timezone.utc).isoformat(),
        "pendingCandidateIds": [street_candidate["id"], parcel_candidate["id"]],
        "sampleReportId": report["id"],
    }
    temporary = MARKER.with_suffix(".tmp")
    temporary.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, MARKER)
    Path("/run/aerocompliance/demo-seed-complete").touch()
    print("Demo scenario: ready (3 completed flights, 4 processed aerial images, "
          "2 pending candidates, 1 labelled sample PDF).", flush=True)


if __name__ == "__main__":
    if os.environ.get("DEMO_SCENARIO_ENABLED") != "1" or MARKER.exists():
        print("Demo scenario: existing volume; no data changed.", flush=True)
    else:
        seed()
