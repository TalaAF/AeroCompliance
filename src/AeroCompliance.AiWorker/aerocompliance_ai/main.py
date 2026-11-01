import hashlib, json, os, tempfile, threading, time, traceback, uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from flask import Flask, jsonify
from minio import Minio
import pika
from .config import Settings
from .model import MockModelRunner, YoloModelRunner, ModelFailure
from . import geospatial

EXCHANGE="aerocompliance.processing"; REQUEST_KEY="image.processing.requested.v1"; STARTED_KEY="image.analysis.started.v1"; COMPLETED_KEY="image.analysis.completed.v1"; FAILED_KEY="image.analysis.failed.v1"; QUEUE="aerocompliance.ai.image-processing.v1"; DLQ="aerocompliance.ai.image-processing.dlq.v1"
ready=False
app=Flask(__name__)
@app.get("/health")
def health(): return jsonify(status="alive")
@app.get("/ready")
def readiness(): return (jsonify(status="ready"),200) if ready else (jsonify(status="starting"),503)
def utcnow(): return datetime.now(timezone.utc).isoformat().replace("+00:00","Z")

class Worker:
    def __init__(self, settings):
        self.s=settings; self.minio=Minio(settings.minio_endpoint,access_key=settings.minio_access_key,secret_key=settings.minio_secret_key,secure=settings.minio_secure)
        self.runner=MockModelRunner() if settings.mode=="mock" else YoloModelRunner(settings)
        # The Pika connection thread must stay free to service heartbeats while YOLO runs. Prefetch
        # is one, so a single analysis executor preserves the worker's original serial semantics.
        self.executor=ThreadPoolExecutor(max_workers=1,thread_name_prefix="aerocompliance-analysis")
    def connection_parameters(self):
        parameters=pika.URLParameters(self.s.rabbitmq_url)
        parameters.heartbeat=self.s.rabbitmq_heartbeat
        parameters.blocked_connection_timeout=max(120,self.s.rabbitmq_heartbeat*2)
        return parameters
    def publish(self,key,payload):
        # Publishing uses a short-lived connection after each long-running stage. Keeping a second
        # BlockingConnection idle during inference would merely move the heartbeat starvation bug.
        conn=pika.BlockingConnection(self.connection_parameters())
        try:
            ch=conn.channel();ch.confirm_delivery();ch.exchange_declare(EXCHANGE,"topic",durable=True)
            eid=payload["eventId"]; published=ch.basic_publish(EXCHANGE,key,json.dumps(payload,separators=(",",":")),pika.BasicProperties(content_type="application/json",delivery_mode=2,message_id=eid,type=key),mandatory=False)
            if published is False: raise RuntimeError("RabbitMQ did not confirm publication")
        finally:
            if conn.is_open: conn.close()
    def callback(self,connection,ch,method,properties,body):
        # Return immediately to start_consuming(); all channel operations are scheduled back onto
        # its I/O thread when the analysis completes.
        self.executor.submit(self.process_delivery,connection,ch,method.delivery_tag,body)
    def finish_delivery(self,connection,ch,delivery_tag,ack,requeue=False):
        def complete():
            if not ch.is_open: return
            if ack: ch.basic_ack(delivery_tag)
            else: ch.basic_nack(delivery_tag,requeue=requeue)
        try: connection.add_callback_threadsafe(complete)
        except pika.exceptions.ConnectionWrongStateError: pass
    def process_delivery(self,connection,ch,delivery_tag,body):
        request=None; started_at=utcnow(); started_clock=time.monotonic()
        try:
            request=json.loads(body); required=("eventId","requestId","imageId","bucket","objectKey","sha256")
            if any(not request.get(k) for k in required): raise ModelFailure("MALFORMED_REQUEST","Required request fields are missing.")
            base={"schemaVersion":1,"correlationId":request["correlationId"],"causationId":request["eventId"],"occurredAt":started_at,"requestId":request["requestId"],"imageId":request["imageId"],"attempt":1}
            self.publish(STARTED_KEY,{**base,"eventId":str(uuid.uuid5(uuid.UUID(request["requestId"]),"started")),"mode":self.s.mode,"modelName":self.s.model_name,"modelVersion":self.s.model_version,"confidenceThreshold":self.s.threshold,"startedAt":started_at})
            # Preserve the object's file extension on the temp file. Ultralytics resolves an
            # image's format from its suffix before reading any bytes, so an extensionless
            # download is rejected outright even though the content is a valid raster.
            suffix=os.path.splitext(request.get("objectKey") or "")[1].lower()
            if suffix not in (".tif",".tiff",".png",".jpg",".jpeg"):
                suffix={"image/tiff":".tif","image/geotiff":".tif","image/png":".png","image/jpeg":".jpg"}.get(
                    (request.get("contentType") or "").lower(), ".tif")
            with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as temp: path=temp.name
            try:
                self.minio.fget_object(request["bucket"],request["objectKey"],path)
                digest=hashlib.sha256()
                with open(path,"rb") as source:
                    for chunk in iter(lambda:source.read(1024*1024),b""): digest.update(chunk)
                if digest.hexdigest().lower()!=request["sha256"].lower(): raise ModelFailure("HASH_MISMATCH","Stored object hash does not match the processing request.")
                # Phase 5: best-effort GeoTIFF raster-metadata extraction, independent of AI_MODE. A
                # missing/invalid raster here never fails *mock* processing (mock doesn't need it) —
                # AI_MODE=yolo's own runner.analyze() call below separately (and fatally) requires it.
                raster=self._extract_raster_metadata(request,path)
                outcome=self.runner.analyze(request,path); completed_at=utcnow()
                self.publish(COMPLETED_KEY,{**base,"eventId":str(uuid.uuid5(uuid.UUID(request["requestId"]),"completed")),"occurredAt":completed_at,"mode":self.s.mode,"modelName":self.s.model_name,"modelVersion":self.s.model_version,"confidenceThreshold":self.s.threshold,
                    "sourceCrs":raster.crs if raster else None,"outputCrs":"EPSG:4326","startedAt":started_at,"completedAt":completed_at,"durationMs":int((time.monotonic()-started_clock)*1000),"detections":outcome.detections,
                    "rasterWidth":raster.width if raster else None,"rasterHeight":raster.height if raster else None,
                    "affineTransform":json.dumps(raster.affine_transform) if raster else None,
                    # Real predictions deliberately kept non-geographic (outside a trusted ROI, or an
                    # ungeoreferenced raster). Reported so nothing the model saw is silently dropped.
                    "modelOnlyDetections":json.dumps(outcome.model_only) if outcome.model_only else None,
                    "modelOnlyCount":len(outcome.model_only),
                    "georeferencingPolicy":json.dumps(outcome.georeferencing) if outcome.georeferencing else None})
            finally:
                if os.path.exists(path): os.remove(path)
            self.finish_delivery(connection,ch,delivery_tag,True)
        except Exception as exc:
            code=exc.code if isinstance(exc,ModelFailure) else "AI_WORKER_ERROR"; message=str(exc)[:500] if isinstance(exc,ModelFailure) else "AI processing failed safely."
            # The event keeps a deliberately generic message (it crosses a trust boundary), but the
            # worker's own log needs the real cause — otherwise an operator sees only
            # "failed safely" and has nothing to debug.
            if not isinstance(exc, ModelFailure):
                print(f"unhandled analysis error for image {(request or {}).get('imageId')}: "
                      f"{type(exc).__name__}: {exc}", flush=True)
                traceback.print_exc()
            if request and request.get("requestId") and request.get("imageId"):
                failed_at=utcnow(); base={"schemaVersion":1,"correlationId":request.get("correlationId",request["requestId"]),"causationId":request.get("eventId"),"occurredAt":failed_at,"requestId":request["requestId"],"imageId":request["imageId"],"attempt":1}
                try:
                    self.publish(FAILED_KEY,{**base,"eventId":str(uuid.uuid5(uuid.UUID(request["requestId"]),"failed")),"stage":"analysis","errorCode":code,"message":message,"retryable":code not in ("MALFORMED_REQUEST","MISSING_CAPTURE_COORDINATES"),"failedAt":failed_at})
                    self.finish_delivery(connection,ch,delivery_tag,True)
                except Exception as publish_exc:
                    print(f"failed to publish analysis failure: {type(publish_exc).__name__}",flush=True)
                    self.finish_delivery(connection,ch,delivery_tag,False,True)
            else: self.finish_delivery(connection,ch,delivery_tag,False,False)
    def _extract_raster_metadata(self, request: dict, path: str):
        """Best-effort GeoTIFF raster-metadata extraction, independent of AI_MODE. Returns None for a
        non-raster source, when rasterio isn't installed (lightweight build), or when the raster
        turns out not to be genuinely georeferenced — logged, never fatal here (mock mode doesn't
        need it; AI_MODE=yolo separately requires it inside runner.analyze()).
        """
        object_key = (request.get("objectKey") or "").lower()
        content_type = (request.get("contentType") or "").lower()
        is_tiff = object_key.endswith((".tif", ".tiff")) or content_type in ("image/tiff", "image/geotiff")
        if not is_tiff:
            return None
        try:
            return geospatial.extract_raster_metadata(path)
        except geospatial.GeoreferencingError as exc:
            print(f"raster metadata unavailable for {request.get('imageId')}: {exc.code}: {exc}", flush=True)
            return None

    def run(self):
        global ready
        while True:
            try:
                conn=pika.BlockingConnection(self.connection_parameters());ch=conn.channel();ch.exchange_declare(EXCHANGE,"topic",durable=True);ch.queue_declare(DLQ,durable=True);ch.queue_bind(DLQ,EXCHANGE,"image.processing.dead.v1");ch.queue_declare(QUEUE,durable=True,arguments={"x-dead-letter-exchange":EXCHANGE,"x-dead-letter-routing-key":"image.processing.dead.v1"});ch.queue_bind(QUEUE,EXCHANGE,REQUEST_KEY);ch.basic_qos(prefetch_count=1)
                if not self.minio.bucket_exists(self.s.minio_bucket): self.minio.make_bucket(self.s.minio_bucket)
                ready=True;ch.basic_consume(QUEUE,lambda channel,method,properties,body:self.callback(conn,channel,method,properties,body),auto_ack=False);ch.start_consuming()
            except Exception as exc: ready=False;print(f"worker connection failed: {type(exc).__name__}",flush=True);time.sleep(3)

def main():
    settings=Settings();threading.Thread(target=lambda:app.run(host="0.0.0.0",port=settings.health_port),daemon=True).start();Worker(settings).run()
if __name__=="__main__": main()
