import json, threading, time
from concurrent.futures import ThreadPoolExecutor
from flask import Flask, jsonify
from minio import Minio
import pika
from .config import Settings
from .model import MockModelRunner, YoloModelRunner
from .job_store import JobStore
from .delivery import EXCHANGE, REQUEST_KEY, QUEUE, DLQ, process_delivery as process_job_delivery
from . import geospatial

ready=False
app=Flask(__name__)
@app.get("/health")
def health(): return jsonify(status="alive")
@app.get("/ready")
def readiness(): return (jsonify(status="ready"),200) if ready else (jsonify(status="starting"),503)

class Worker:
    def __init__(self, settings):
        self.s=settings; self.minio=Minio(settings.minio_endpoint,access_key=settings.minio_access_key,secret_key=settings.minio_secret_key,secure=settings.minio_secure)
        self.runner=MockModelRunner() if settings.mode=="mock" else YoloModelRunner(settings)
        self.jobs=JobStore(settings.database_connection_string)
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
            eid=payload["eventId"]; published=ch.basic_publish(EXCHANGE,key,json.dumps(payload,separators=(",",":")),pika.BasicProperties(content_type="application/json",delivery_mode=2,message_id=eid,type=key),mandatory=True)
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
        return process_job_delivery(self,connection,ch,delivery_tag,body)
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
                with self.jobs.connect() as database:
                    database.execute("SELECT request_id FROM ai_processing_jobs LIMIT 0")
                ready=True;ch.basic_consume(QUEUE,lambda channel,method,properties,body:self.callback(conn,channel,method,properties,body),auto_ack=False);ch.start_consuming()
            except Exception as exc: ready=False;print(f"worker connection failed: {type(exc).__name__}",flush=True);time.sleep(3)

def main():
    settings=Settings();threading.Thread(target=lambda:app.run(host="0.0.0.0",port=settings.health_port),daemon=True).start();Worker(settings).run()
if __name__=="__main__": main()
