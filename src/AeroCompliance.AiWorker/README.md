# AeroCompliance AI Worker

Runs in `AI_MODE=mock` by default and consumes RabbitMQ processing requests. Health endpoints are
`/health` and `/ready` on port 8080.

## Dependency profiles

Three explicit profiles, selected by the `AI_REQUIREMENTS` Docker build arg (default
`requirements.txt`):

| Profile file | Layers on | Adds | Needs a `.pt` file? | Needs CUDA? |
|---|---|---|---|---|
| `requirements.txt` (**core/mock**) | — | pika, minio, flask | No | No |
| `requirements-geospatial.txt` | core | `rasterio`, `shapely`, `pyproj` | No | No |
| `requirements-yolo.txt` | geospatial | `ultralytics` (pulls PyTorch) | Only to actually run inference | Not required to build/import; used automatically if a GPU is present |

Each file only lists what it *adds* over the one before it (`-r <previous>.txt` at the top) — no
duplicated version pins across files.

**AppHost selects the profile from `AEROCOMPLIANCE_AI_MODE`.** The default `mock` launch builds the
geospatial profile. Setting `AEROCOMPLIANCE_AI_MODE=yolo` builds the YOLO profile and mounts
`models/best.pt` read-only. No source edit is needed.

- **`"requirements.txt"`** — smallest/fastest image; mock detection only, no GeoTIFF raster metadata
  (a `.tif` upload still completes in mock mode, just with `SourceRasterCrs`/`RasterWidth`/
  `RasterHeight`/`AffineTransform` left `null`).
- **`"requirements-geospatial.txt"`** — the default; adds real GeoTIFF metadata extraction.
- **`"requirements-yolo.txt"`** — geospatial + real YOLO inference once a `.pt` model is supplied and
  `AI_MODE=yolo` is set (see "Real model handoff" in the root README).

Building directly with plain `docker`:

```powershell
docker build --build-arg AI_REQUIREMENTS=requirements-geospatial.txt -t aerocompliance-ai-worker .
```

**Verified in this workspace**: the core/geospatial profiles build and the YOLO profile builds and
runs using CPU-only PyTorch. On 2026-10-05 the mounted checkpoint processed an un-georeferenced PNG
in the live AppHost pipeline and returned 48 model-only predictions (45 building, 3 road), zero
geographic detections and zero candidates. This verifies inference and the safety boundary, not
accuracy or authoritative geographic processing.

The Dockerfile also installs `libexpat1` unconditionally (a small `apt-get` layer) — rasterio's wheel
dynamically loads it at import time, and `python:3.12-slim` does not ship it. Without this,
`import rasterio` raises `ImportError: libexpat.so.1: cannot open shared object file`, which
`geospatial.py`'s `try/except ImportError` catches silently (`HAS_RASTERIO=False`) — the worker would
still start and mock-only processing would still work, it would just silently lose GeoTIFF metadata
extraction. This was found by actually building and running the geospatial image, not by inspection.

## Metadata extraction

Independent of `AI_MODE`, whenever the source object is a `.tif`/`.tiff` the worker attempts to read
its **raster georeferencing** with `rasterio` (`aerocompliance_ai/geospatial.py`) — present whenever
the image was built with the `geospatial` or `yolo` profile:

- CRS, affine transform, width, height, bounds, and pixel resolution.
- A raster with no CRS, no transform, or only a default identity transform raises
  `MISSING_GEOREFERENCING`; a transform/bounds containing non-finite values raises
  `INVALID_GEOREFERENCING`. Neither is invented or silently ignored.

In **`AI_MODE=mock`** this is best-effort only: a missing/invalid raster (or `rasterio` simply not
being installed, on the `core` profile) is logged and simply means no raster metadata is attached to
the `image.analysis.completed.v1` event — it never fails mock processing (mock detections don't need
it). In **`AI_MODE=yolo`**, missing georeferencing does not fail inference: predictions are retained
as model-only, with no invented geometry and no geographic compliance evaluation.

**A GeoTIFF's raster georeferencing is not the same fact as a JPEG's EXIF GPS tag.** EXIF GPS locates
the camera/drone at one approximate point; a GeoTIFF's CRS + affine transform georeferences every
pixel precisely enough to convert a detection mask into map coordinates. `ProcessingService` tracks
these with different provenance values (`"GeoTiff"` vs. `"Exif"`) on the `Image` row rather than
treating them as equivalent — see the root README's "Automatic metadata extraction" section.

**Verified through the full running system** (real HTTP upload → MinIO → Outbox → RabbitMQ → this
worker, built with the `geospatial` profile, running under AppHost → `ProcessingService` →
PostgreSQL): a genuinely georeferenced synthetic GeoTIFF's CRS (`EPSG:32636`), width, height, and
affine transform were correctly read and persisted onto the `Image` row; a non-georeferenced TIFF
completed processing safely with those four fields left `null` (not invented, not a crash); no image
with a missing location ever received a default/invented coordinate (checked directly in
PostgreSQL). Reprocessing the same image is still governed by the pre-existing Inbox/Outbox and
unique-constraint idempotency in `ProcessingService` and `ImagesController` — unchanged by this work.

## Real YOLO — what is and is not done here

`YoloModelRunner` (`aerocompliance_ai/model.py`) lazily imports `ultralytics` and loads
`AI_MODEL_PATH` (AppHost sets `/models/best.pt`) only when `AI_MODE=yolo` calls `analyze()`.
It runs the model on supported imagery; when a usable raster georeference exists, it converts each
detection's pixel-space mask (or bounding box, if the model has no segmentation head) into an
`EPSG:4326` `MultiPolygon` via
`geospatial.polygon_pixels_to_wgs84` / `build_multipolygon_geojson`, then returns detections in the
exact shape `MockModelRunner` already produces — so no .NET contract changes are needed to switch.

**Status: the real model is now integrated and has been run.** `models/best.pt`
(sha256 `577ec6f619b5e22a462b0ac62d5a55722cded832ea07fbaf70f01d93786479f8`) was inspected and
executed against the supplied validation imagery. Verified facts, taken from the checkpoint itself
rather than the training notes:

| Property | Verified value |
|---|---|
| Task | `segment` |
| Classes | `{0: "building", 1: "road"}` — **two**, not three |
| Base model | `yolo26m-seg`, trained at `imgsz=1024`, 200 epochs |
| Saved by | Ultralytics `8.4.131` |

Two corrections this surfaced:

* `GeoDemoGuardai/Models/model_info.json` and `data.yaml` both claim a third `violation` class.
  The shipped weights do not contain it. Anything relying on class id `2` would never fire.
* The `Segment26` head requires **ultralytics >= 8.4.131**; on the 8.3.x line the checkpoint fails
  to load with `Can't get attribute 'Segment26'`. `requirements-yolo.txt` pins the floor accordingly.

### Georeferencing policy

`YoloModelRunner` emits geographic geometry only when the source raster genuinely carries a CRS and
affine transform. For a raster without them the model still runs — the detection count/classes and
model-only details are persisted on the analysis, but **no geographic geometry is produced**, because
converting pixels to latitude/longitude without a transform would fabricate location data. Such an
image completes successfully with zero geographic detections rather than failing.

This matters for the currently supplied validation set: none of the 15 images carries
georeferencing (`ramallah_28191_new.tif` has no GeoTIFF tags at all despite its name), so real
geographic end-to-end processing cannot be demonstrated with them. A genuinely georeferenced
GeoTIFF covering the Ramallah GIS extent is the one missing input.

### Running it

1. Build the `yolo` profile: `--build-arg AI_REQUIREMENTS=requirements-yolo.txt`. The Dockerfile
   installs CPU-only PyTorch from PyTorch's CPU wheel index — no CUDA is required or pulled — plus
   the OpenCV runtime libraries ultralytics links against.
2. The `.pt` file is **never copied into the image** (`.dockerignore` excludes `models/*.pt`);
   AppHost bind-mounts it read-only at `/models/best.pt`.
3. AppHost selects `mock` by default; set `AEROCOMPLIANCE_AI_MODE=yolo` to run real inference without
   editing code.
