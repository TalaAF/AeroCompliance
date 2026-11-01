import os
import uuid
from dataclasses import dataclass, field

from . import geospatial


class ModelFailure(Exception):
    def __init__(self, code: str, message: str): super().__init__(message); self.code = code


@dataclass
class AnalysisOutcome:
    """What one inference produced.

    ``detections`` carry geographic geometry and may be evaluated for compliance. ``model_only``
    are real predictions deliberately left non-geographic -- outside a trusted ROI, or from a
    raster with no usable transform. They are reported rather than discarded so an operator can
    see everything the model actually saw.
    """

    detections: list = field(default_factory=list)
    model_only: list = field(default_factory=list)
    georeferencing: dict | None = None


class MockModelRunner:
    def analyze(self, request: dict, image_path: str | None = None) -> AnalysisOutcome:
        # image_path is accepted (and ignored) for interface parity with YoloModelRunner — the mock
        # never inspects file bytes, only the request's own captured coordinates/scenario.
        scenario = (request.get("mockScenario") or "none").lower()
        if scenario == "failure": raise ModelFailure("MOCK_FAILURE", "The requested deterministic mock failure occurred.")
        if scenario == "none": return AnalysisOutcome()
        lon, lat = request.get("longitude"), request.get("latitude")
        if lon is None or lat is None: raise ModelFailure("MISSING_CAPTURE_COORDINATES", "Geographic mock scenarios require capture longitude and latitude.")
        delta = 0.00008
        ring = [[lon-delta,lat-delta],[lon+delta,lat-delta],[lon+delta,lat+delta],[lon-delta,lat+delta],[lon-delta,lat-delta]]
        detection_id = str(uuid.uuid5(uuid.UUID(request["requestId"]), "detection:0"))
        road = scenario == "road"
        return AnalysisOutcome(detections=[{
            "detectionId": detection_id, "classId": 1 if road else 0,
            "className": "road" if road else "building", "confidence": 0.91 if road else 0.93,
            "geometry": {"type": "MultiPolygon", "coordinates": [[[p for p in ring]]]},
            # The deterministic demonstration path is exact by construction, not an estimate.
            "resultSource": geospatial.SOURCE_MOCK_GEOGRAPHIC,
            "isApproximate": False, "requiresManualReview": False,
            "georeferencingConfidence": "Deterministic",
            "sourceGeoreferencingMethod": "MockCaptureCoordinates",
        }])


class YoloModelRunner:
    """Loads a YOLO/Ultralytics model lazily and runs real segmentation inference.

    Verified against the shipped model (models/best.pt, sha256 577ec6f6...):
      * task = "segment", 2 classes -> {0: "building", 1: "road"}
      * trained from yolo26m-seg at imgsz 1024, checkpoint written by Ultralytics 8.4.131
      * the Segment26 head requires ultralytics >= 8.4.131 (see requirements-yolo.txt)

    Georeferencing policy. Detections are only ever emitted with geographic geometry when the
    source raster actually carries a CRS and affine transform. For a raster without them the model
    still runs -- the count and classes are real and are logged -- but no geometry is produced,
    because turning pixel coordinates into latitude/longitude without a transform would invent
    location data. Such an image completes successfully with zero geographic detections rather than
    failing, so the operator sees an honest "nothing geographic could be derived" result.
    """

    def __init__(self, settings):
        self.settings = settings
        self._model = None  # Lazy: only import/load ultralytics the first time analyze() needs it.

    def _load(self):
        if self._model is not None:
            return self._model
        try:
            from ultralytics import YOLO
        except ImportError as exc:
            raise RuntimeError(
                "AI_MODE=yolo requires the ultralytics package. Rebuild the worker image with "
                "requirements-yolo.txt (see the AI Worker README) before selecting this mode."
            ) from exc
        if not os.path.isfile(self.settings.model_path):
            raise RuntimeError(
                f"AI_MODEL_PATH does not point to an existing file: {self.settings.model_path}. "
                "Mount/copy the teammate's .pt weights there before selecting AI_MODE=yolo."
            )
        self._model = YOLO(self.settings.model_path)
        return self._model

    def analyze(self, request: dict, image_path: str | None = None) -> AnalysisOutcome:
        if image_path is None:
            raise ModelFailure("MISSING_IMAGE", "No local image file was provided to the YOLO runner.")
        if not geospatial.HAS_RASTERIO:
            raise ModelFailure(
                "RASTERIO_UNAVAILABLE",
                "AI_MODE=yolo requires rasterio for georeferencing. Rebuild the worker image with "
                "requirements-yolo.txt (see the AI Worker README) before selecting this mode.",
            )

        # A raster without CRS + affine transform is not a failure of the model or the pipeline --
        # it simply cannot yield map coordinates. Inference still runs so the real classes and
        # counts are known and logged, but nothing geographic is emitted.
        raster = None
        try:
            raster = geospatial.extract_raster_metadata(image_path)
        except geospatial.GeoreferencingError as exc:
            print(
                f"image {request.get('imageId')} is not georeferenced ({exc.code}): "
                "running model-only inference; no geographic detections will be produced.",
                flush=True,
            )

        model = self._load()
        results = model.predict(image_path, conf=float(self.settings.threshold), verbose=False)
        if not results:
            return AnalysisOutcome()

        result = results[0]
        boxes = result.boxes
        masks = result.masks
        if boxes is None or len(boxes) == 0:
            return AnalysisOutcome()
        names = getattr(result, "names", {}) or {}

        # A raster may declare its own georeference as approximate and name the region it is
        # trusted in. Outside that region the prediction is still real, but its map position is
        # not, so it stays model-only.
        policy = geospatial.read_experimental_policy(raster.tags) if raster else None
        georeferencing = None
        if policy is not None:
            georeferencing = {
                "status": "Estimated", "method": policy.method,
                "confidence": policy.confidence, "eligibility": policy.eligibility,
                "requiresManualReview": policy.requires_manual_review,
                "roiRadiusMetres": policy.roi_radius_m,
                "roiAnchorX": policy.anchor_x, "roiAnchorY": policy.anchor_y,
                "roiCrs": raster.crs,
            }
            print(
                f"image {request.get('imageId')}: raster declares an ESTIMATED georeference "
                f"({policy.method}); geographic evaluation restricted to "
                f"{policy.roi_radius_m} m around ({policy.anchor_x}, {policy.anchor_y}) "
                f"in {raster.crs}.",
                flush=True,
            )

        detections: list[dict] = []
        model_only: list[dict] = []

        for index in range(len(boxes)):
            class_id = int(boxes.cls[index].item())
            confidence = float(boxes.conf[index].item())
            class_name = str(names.get(class_id, str(class_id)))
            x1, y1, x2, y2 = (float(v) for v in boxes.xyxy[index])
            centre_px = ((x1 + x2) / 2.0, (y1 + y2) / 2.0)

            def as_model_only(reason: str, distance=None):
                return {
                    "detectionId": str(uuid.uuid5(uuid.UUID(request["requestId"]), f"detection:{index}")),
                    "classId": class_id, "className": class_name,
                    "confidence": round(confidence, 4),
                    "pixelCentroid": [round(centre_px[0], 1), round(centre_px[1], 1)],
                    "pixelBbox": [round(v, 1) for v in (x1, y1, x2, y2)],
                    "reason": reason,
                    "distanceFromAnchorMetres": None if distance is None else round(distance, 2),
                    "resultSource": geospatial.SOURCE_REAL_YOLO_MODEL_ONLY,
                    "isApproximate": False, "requiresManualReview": False,
                    "georeferencingConfidence": "NotGeoreferenced",
                }

            if raster is None:
                # Real prediction, but no transform exists to place it on the map.
                model_only.append(as_model_only("NoRasterGeoreferencing"))
                continue

            if policy is not None and policy.has_roi:
                cx, cy = geospatial.pixel_to_raster(raster.affine_transform, *centre_px)
                distance = policy.distance_from_anchor(cx, cy)
                if not policy.contains(cx, cy):
                    model_only.append(as_model_only("OutsideTrustedRoi", distance))
                    continue
            else:
                distance = None

            if masks is not None and index < len(masks.xy):
                pixel_ring = [(float(x), float(y)) for x, y in masks.xy[index]]
            else:
                # No segmentation mask for this detection (a detect-only model/head) — fall back to
                # its bounding box rather than dropping the detection.
                pixel_ring = [(x1, y1), (x2, y1), (x2, y2), (x1, y2)]

            if len(pixel_ring) < 3:
                continue  # Degenerate mask/box for this one detection — skip only it.
            if pixel_ring[0] != pixel_ring[-1]:
                pixel_ring = [*pixel_ring, pixel_ring[0]]

            wgs84_rings = geospatial.polygon_pixels_to_wgs84(raster.affine_transform, raster.crs, [pixel_ring])
            geometry = geospatial.build_multipolygon_geojson([wgs84_rings])
            if geometry is None:
                continue

            approximate = policy is not None
            detections.append({
                "detectionId": str(uuid.uuid5(uuid.UUID(request["requestId"]), f"detection:{index}")),
                "classId": class_id,
                "className": class_name,
                "confidence": round(confidence, 4),
                "geometry": geometry,
                "resultSource": geospatial.SOURCE_REAL_YOLO_ESTIMATED if approximate
                                else geospatial.SOURCE_REAL_YOLO_GEOREFERENCED,
                "isApproximate": approximate,
                "requiresManualReview": policy.requires_manual_review if approximate else False,
                "georeferencingConfidence": policy.confidence if approximate else "Surveyed",
                "sourceGeoreferencingMethod": policy.method if approximate else "RasterAffine",
                "distanceFromAnchorMetres": None if distance is None else round(distance, 2),
            })

        if model_only:
            summary: dict = {}
            for item in model_only:
                summary[item["className"]] = summary.get(item["className"], 0) + 1
            print(
                f"image {request.get('imageId')}: {len(model_only)} model-only detection(s) "
                f"{summary} retained without geographic geometry "
                f"({model_only[0]['reason']}); {len(detections)} geographic detection(s).",
                flush=True,
            )
        return AnalysisOutcome(detections=detections, model_only=model_only,
                               georeferencing=georeferencing)
