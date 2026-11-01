"""Read-only smoke checks for a fresh demo volume; --dismiss reviews one test case.

Run inside a disposable container after copying this file to /tmp. This script is
not part of the image or its startup path.
"""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path


parser = argparse.ArgumentParser()
parser.add_argument("--dismiss", action="store_true")
args = parser.parse_args()

config = Path("/var/lib/aerocompliance/config/runtime.env")
for line in config.read_text().splitlines():
    if line.startswith("DEMO_PASSWORD="):
        os.environ["DEMO_PASSWORD"] = line.partition("=")[2]
        break
assert "DEMO_PASSWORD" in os.environ

spec = importlib.util.spec_from_file_location("demo_seed", "/opt/aerocompliance/demo-seed.py")
demo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(demo)
demo.login()
marker = json.loads(demo.MARKER.read_text())
flights = demo.page("/api/flights?pageSize=100")
scenario_flights = [f for f in flights if (f.get("missionName") or "").startswith("SYNTHETIC DEMO")]
assert len(scenario_flights) == 4, scenario_flights
assert sum(f["status"] == "completed" for f in scenario_flights) == 3
assert sum(f["status"] == "planned" for f in scenario_flights) == 1

images = []
for flight in scenario_flights:
    images.extend(i for i in demo.page(f"/api/flights/{flight['id']}/images?pageSize=100")
                  if i["originalFileName"].startswith("SYNTHETIC-DEMO"))
assert len(images) == 4, images
detections = []
for item in images:
    detections.extend(demo.page(f"/api/detections?imageId={item['id']}&pageSize=100"))
assert {d["className"] for d in detections} >= {"building", "road"}
assert all(d["resultSource"] == "MockGeographicScenario" for d in detections)

pending_id, review_id = marker["pendingCandidateIds"]
pending = demo.request("GET", f"/api/violations/{pending_id}")
review = demo.request("GET", f"/api/violations/{review_id}")
assert pending["status"] == "under_review"
if args.dismiss and review["status"] == "under_review":
    review = demo.request("POST", f"/api/violations/{review_id}/dismiss", {
        "notes": "SYNTHETIC DEMO TEST: dismissed through normal review workflow on a disposable volume."
    })
assert review["status"] == ("dismissed" if args.dismiss else review["status"])
assert review["status"] in ("under_review", "dismissed")
if args.dismiss:
    assert any(h["status"] == "dismissed" and "SYNTHETIC DEMO TEST" in h["notes"]
               for h in review["statusHistory"]), review["statusHistory"]

report_id = marker["sampleReportId"]
report = demo.request("GET", f"/api/reports/{report_id}")
pdf = demo.request("GET", f"/api/reports/{report_id}/download", raw=True)
assert pdf.startswith(b"%PDF-") and len(pdf) > 1000
assert any(r["id"] == report_id for r in demo.page("/api/reports?pageSize=100"))

print(json.dumps({
    "architecture": os.uname().machine,
    "flightStatuses": {f["missionName"]: f["status"] for f in scenario_flights},
    "imageCount": len(images),
    "detectionClasses": sorted({d["className"] for d in detections}),
    "detectionSource": sorted({d["resultSource"] for d in detections}),
    "pendingCandidateStatus": pending["status"],
    "reviewCandidateStatus": review["status"],
    "reviewHistoryCount": len(review["statusHistory"]),
    "reportId": report_id,
    "reportSource": report.get("generatedBy"),
    "pdfBytes": len(pdf),
    "credentialsSha256": hashlib.sha256(config.read_bytes()).hexdigest(),
}, indent=2))
