"""Exercise one real or mock processing job inside a disposable demo container."""

import argparse
import hashlib
import importlib.util
import json
import os
import time
from datetime import datetime, timezone
from pathlib import Path


parser = argparse.ArgumentParser()
parser.add_argument("mode", choices=("yolo", "mock"))
parser.add_argument("--suffix", default="")
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

flight = next(f for f in demo.page("/api/flights?pageSize=100")
              if f["missionName"] == "SYNTHETIC DEMO - Street and rooftop survey")
filename = f"MODE-TEST-{args.mode}{args.suffix}-building-street.png"
images = demo.page(f"/api/flights/{flight['id']}/images?pageSize=100")
existing = next((i for i in images if i["originalFileName"] == filename), None)
if existing:
    image_id = existing["id"]
else:
    fields = {"capturedAt": datetime.now(timezone.utc).isoformat()}
    if args.mode == "mock":
        fields.update(mockScenario="road", longitude="35.20000", latitude="31.90000")
    body, content_type = demo.multipart_image(
        demo.ASSETS / "synthetic-building-street.png", filename, fields)
    receipt = demo.request("POST", f"/api/flights/{flight['id']}/images", body,
                           headers={"Content-Type": content_type})
    image_id = receipt["imageId"]

deadline = time.monotonic() + 420
while time.monotonic() < deadline:
    status = demo.request("GET", f"/api/images/{image_id}/processing-status")
    if status["status"].lower() == "completed":
        break
    if status["status"].lower() in ("analysisfailed", "compliancefailed"):
        raise RuntimeError(f"Processing failed: {status['errorCode']} {status['errorMessage']}")
    time.sleep(3)
else:
    raise TimeoutError(f"{args.mode} processing did not finish")

analysis = status["analysis"]
assert analysis["mode"] == args.mode, analysis
assert analysis["status"].lower() == "completed", analysis
if args.mode == "yolo":
    assert "yolo" in analysis["modelName"].lower(), analysis
    assert analysis["geographicDetections"] == 0, analysis
else:
    assert "mock" in analysis["modelName"].lower(), analysis
    detections = demo.page(f"/api/detections?imageId={image_id}&pageSize=100")
    assert detections and all(d["resultSource"] == "MockGeographicScenario" for d in detections)

print(json.dumps({
    "architecture": os.uname().machine,
    "imageId": image_id,
    "mode": analysis["mode"],
    "modelName": analysis["modelName"],
    "processingStatus": status["status"],
    "totalDetectionCount": analysis["totalDetections"],
    "geographicDetectionCount": analysis["geographicDetections"],
    "modelOnlyDetectionCount": analysis["modelOnlyDetections"],
    "credentialsSha256": hashlib.sha256(config.read_bytes()).hexdigest(),
}, indent=2))
