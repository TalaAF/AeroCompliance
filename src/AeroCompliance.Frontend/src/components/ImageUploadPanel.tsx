import { CheckCircle2, MapPin, RotateCcw, Upload, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { api, errorMessage } from "../api/client";
import type { ImageReceipt, ProcessingStatus } from "../api/types";
import { readExifPreview, type ExifPreview } from "../lib/exifPreview";
import GeoMap from "./GeoMap";
import { Notice, Status } from "./Common";

const MAX_CONCURRENT_UPLOADS = 2;
const POLL_MS = 1500;
const terminal = new Set(["Completed", "AnalysisFailed", "ComplianceFailed", "UploadFailed"]);

type FileState =
  | "Waiting"
  | "Uploading"
  | "Queued"
  | "Processing"
  | "Completed"
  | "NeedsReview"
  | "Failed";

interface QueuedFile {
  key: string;
  file: File;
  exif: ExifPreview | null;
  exifChecked: boolean;
  state: FileState;
  progress: number;
  error: string | null;
  imageId: string | null;
  status: ProcessingStatus | null;
  receipt: ImageReceipt | null;
}

function fmtBytes(n: number) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

export default function ImageUploadPanel({
  flightId,
  onProcessed,
}: {
  flightId: string;
  onProcessed: () => void;
}) {
  const [queue, setQueue] = useState<QueuedFile[]>([]);
  const [mockScenario, setMockScenario] = useState("none");
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [showMap, setShowMap] = useState(false);
  const [pickedPoint, setPickedPoint] = useState<{ lat: number; lon: number } | null>(null);
  const [manual, setManual] = useState({ capturedAt: "", altitudeM: "", cameraHeadingDeg: "" });
  const [error, setError] = useState("");
  const fileRef = useRef<HTMLInputElement>(null);
  const activeUploads = useRef(0);
  const pollers = useRef(new Set<string>());

  // Client-side EXIF preview only — a convenience so the user sees what the camera recorded
  // immediately. The server independently re-extracts this from the uploaded bytes.
  const addFiles = async (list: FileList | null) => {
    if (!list || list.length === 0) return;
    const added: QueuedFile[] = Array.from(list).map((file) => ({
      key: `${file.name}-${file.size}-${file.lastModified}-${Math.random().toString(36).slice(2)}`,
      file,
      exif: null,
      exifChecked: false,
      state: "Waiting",
      progress: 0,
      error: null,
      imageId: null,
      status: null,
      receipt: null,
    }));
    setQueue((q) => [...q, ...added]);
    for (const item of added) {
      const exif = await readExifPreview(item.file);
      setQueue((q) => q.map((x) => (x.key === item.key ? { ...x, exif, exifChecked: true } : x)));
    }
  };

  const removeFile = (key: string) => setQueue((q) => q.filter((x) => x.key !== key));

  const uploadOne = async (item: QueuedFile) => {
    setQueue((q) => q.map((x) => (x.key === item.key ? { ...x, state: "Uploading", error: null, progress: 0 } : x)));
    const body = new FormData();
    body.append("file", item.file);
    const noExifLocation = !item.exif?.latitude || !item.exif?.longitude;
    if (noExifLocation && pickedPoint) {
      body.append("latitude", String(pickedPoint.lat));
      body.append("longitude", String(pickedPoint.lon));
      body.append("overrideLocation", "true");
    }
    if (manual.capturedAt) body.append("capturedAt", new Date(manual.capturedAt).toISOString());
    if (manual.altitudeM) body.append("altitudeM", manual.altitudeM);
    if (manual.cameraHeadingDeg) body.append("cameraHeadingDeg", manual.cameraHeadingDeg);
    if (mockScenario !== "none") body.append("mockScenario", mockScenario);
    try {
      const { data } = await api.post<ImageReceipt>(`/flights/${flightId}/images`, body, {
        onUploadProgress: (evt) => {
          const pct = evt.total ? Math.round((evt.loaded / evt.total) * 100) : 0;
          setQueue((q) => q.map((x) => (x.key === item.key ? { ...x, progress: pct } : x)));
        },
      });
      setQueue((q) =>
        q.map((x) =>
          x.key === item.key
            ? {
                ...x,
                state: data.metadataRequiresReview ? "NeedsReview" : "Queued",
                imageId: data.imageId,
                receipt: data,
                progress: 100,
              }
            : x,
        ),
      );
      pollStatus(item.key, data.imageId);
    } catch (e) {
      setQueue((q) =>
        q.map((x) => (x.key === item.key ? { ...x, state: "Failed", error: errorMessage(e) } : x)),
      );
    } finally {
      activeUploads.current -= 1;
    }
  };

  const pollStatus = (key: string, imageId: string) => {
    if (pollers.current.has(imageId)) return;
    pollers.current.add(imageId);
    const tick = async () => {
      try {
        const { data } = await api.get<ProcessingStatus>(`/images/${imageId}/processing-status`);
        setQueue((q) =>
          q.map((x) =>
            x.key === key
              ? {
                  ...x,
                  status: data,
                  state: terminal.has(data.status)
                    ? data.status === "Completed"
                      ? x.state === "NeedsReview"
                        ? "NeedsReview" // keep the reviewer flag visible even once processing finished
                        : "Completed"
                      : "Failed"
                    : "Processing",
                }
              : x,
          ),
        );
        if (terminal.has(data.status)) {
          pollers.current.delete(imageId);
          onProcessed();
          return;
        }
      } catch {
        pollers.current.delete(imageId);
        return;
      }
      window.setTimeout(tick, POLL_MS);
    };
    void tick();
  };

  // Controlled-concurrency runner: picks up "Waiting" files whenever a slot frees up.
  useEffect(() => {
    if (activeUploads.current >= MAX_CONCURRENT_UPLOADS) return;
    const next = queue.find((x) => x.state === "Waiting");
    if (!next) return;
    activeUploads.current += 1;
    void uploadOne(next);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queue]);

  const retryUpload = (key: string) =>
    setQueue((q) => q.map((x) => (x.key === key ? { ...x, state: "Waiting", error: null } : x)));

  const retryProcessing = async (key: string, imageId: string) => {
    try {
      await api.post(`/images/${imageId}/retry-processing`);
      setQueue((q) => q.map((x) => (x.key === key ? { ...x, state: "Queued", error: null } : x)));
      pollStatus(key, imageId);
    } catch (e) {
      setError(errorMessage(e));
    }
  };

  return (
    <div>
      <label className="upload-zone">
        Images (.tif/.tiff; PNG/JPEG accepted in Development) — select one or more
        <input
          ref={fileRef}
          type="file"
          multiple
          accept=".tif,.tiff,.png,.jpg,.jpeg,image/tiff,image/png,image/jpeg"
          onChange={(e) => {
            void addFiles(e.target.files);
            if (fileRef.current) fileRef.current.value = "";
          }}
        />
      </label>
      {error && <Notice kind="error">{error}</Notice>}

      <div className="actions" style={{ marginTop: 10 }}>
        <button type="button" className="secondary" onClick={() => setShowMap((v) => !v)}>
          <MapPin size={14} />
          {pickedPoint ? "Change fallback location" : "Pick fallback location"}
        </button>
        <button type="button" className="secondary" onClick={() => setShowAdvanced((v) => !v)}>
          {showAdvanced ? "Hide" : "Show"} advanced/manual fields
        </button>
      </div>

      {showMap && (
        <div style={{ marginTop: 10 }}>
          <p style={{ fontSize: 12, color: "#64748b" }}>
            Click the map to set an <strong>approximate, manual</strong> location. It is only used
            for files that have no GPS location of their own (from EXIF) — a file with its own
            location keeps it.
          </p>
          <GeoMap
            points={pickedPoint ? [] : []}
            pickedPoint={pickedPoint}
            onPick={(lat, lon) => setPickedPoint({ lat, lon })}
            height={220}
          />
        </div>
      )}

      {showAdvanced && (
        <div className="field-row three" style={{ marginTop: 10 }}>
          <label>
            Captured at (optional — overrides EXIF)
            <input
              type="datetime-local"
              value={manual.capturedAt}
              onChange={(e) => setManual({ ...manual, capturedAt: e.target.value })}
            />
          </label>
          <label>
            Altitude (m)
            <input
              type="number"
              step="any"
              value={manual.altitudeM}
              onChange={(e) => setManual({ ...manual, altitudeM: e.target.value })}
            />
          </label>
          <label>
            Heading (°)
            <input
              type="number"
              step="any"
              value={manual.cameraHeadingDeg}
              onChange={(e) => setManual({ ...manual, cameraHeadingDeg: e.target.value })}
            />
          </label>
          <label>
            <span>
              Mock scenario <em className="mock-label">Development-only</em>
            </span>
            <select value={mockScenario} onChange={(e) => setMockScenario(e.target.value)}>
              <option value="none">None</option>
              <option value="building">Building</option>
              <option value="road_encroachment">Road encroachment</option>
              <option value="road">Road feature (mock)</option>
              <option value="parcel_encroachment">Parcel encroachment</option>
              <option value="failure">Failure/retry check</option>
            </select>
          </label>
        </div>
      )}

      {queue.length > 0 && (
        <div className="table-wrap file-queue" style={{ marginTop: 14 }}>
          <table className="data-table">
            <thead>
              <tr>
                <th>File</th>
                <th>Detected location</th>
                <th>State</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {queue.map((x) => (
                <tr key={x.key}>
                  <td>
                    {x.file.name}
                    <small>
                      {x.file.type || "unknown type"} · {fmtBytes(x.file.size)}
                      {x.state === "Uploading" ? ` · ${x.progress}%` : ""}
                    </small>
                  </td>
                  <td>
                    {!x.exifChecked ? (
                      "…"
                    ) : x.exif?.latitude && x.exif?.longitude ? (
                      <span className="badge-source exif">Drone metadata</span>
                    ) : x.receipt?.locationSource === "GeoTiff" ? (
                      <span className="badge-source geotiff">GeoTIFF</span>
                    ) : pickedPoint ? (
                      <span className="badge-source">Selected on map</span>
                    ) : (
                      <span className="badge-source missing">No location found</span>
                    )}
                  </td>
                  <td>
                    <Status value={x.state} />
                    {x.status?.errorMessage && (
                      <div style={{ fontSize: 11, color: "#b91c1c", marginTop: 4 }}>
                        {x.status.errorMessage}
                      </div>
                    )}
                    {x.error && (
                      <div style={{ fontSize: 11, color: "#b91c1c", marginTop: 4 }}>{x.error}</div>
                    )}
                  </td>
                  <td>
                    <div className="actions">
                      {x.state === "Failed" && !x.imageId && (
                        <button type="button" className="secondary" onClick={() => retryUpload(x.key)}>
                          <RotateCcw size={13} />
                          Retry upload
                        </button>
                      )}
                      {x.state === "Failed" && x.imageId && (
                        <button
                          type="button"
                          className="secondary"
                          onClick={() => void retryProcessing(x.key, x.imageId!)}
                        >
                          <RotateCcw size={13} />
                          Retry processing
                        </button>
                      )}
                      {x.state === "Completed" && <CheckCircle2 size={15} color="#047857" />}
                      {(x.state === "Waiting" || x.state === "Failed") && (
                        <button type="button" className="secondary" onClick={() => removeFile(x.key)}>
                          <X size={13} />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {queue.length === 0 && (
        <p style={{ fontSize: 12, color: "#94a3b8", marginTop: 10 }}>
          <Upload size={12} /> No files selected yet.
        </p>
      )}
    </div>
  );
}
