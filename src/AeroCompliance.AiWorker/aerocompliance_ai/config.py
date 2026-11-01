import os
from dataclasses import dataclass

@dataclass(frozen=True)
class Settings:
    mode: str = os.getenv("AI_MODE", "mock").lower()
    rabbitmq_url: str = os.getenv("RABBITMQ_URL") or os.environ["ConnectionStrings__messaging"]
    rabbitmq_heartbeat: int = int(os.getenv("RABBITMQ_HEARTBEAT_SECONDS", "60"))
    database_connection_string: str = os.environ["ConnectionStrings__aerocompliancedb"]
    minio_endpoint: str = os.environ["MINIO_ENDPOINT"].replace("http://", "").replace("https://", "")
    minio_access_key: str = os.environ["MINIO_ACCESS_KEY"]
    minio_secret_key: str = os.environ["MINIO_SECRET_KEY"]
    minio_secure: bool = os.getenv("MINIO_SECURE", "false").lower() == "true"
    minio_bucket: str = os.getenv("MINIO_BUCKET", "aerocompliance-images")
    model_path: str = os.getenv("AI_MODEL_PATH", "/models/model.pt")
    threshold: float = float(os.getenv("AI_CONFIDENCE_THRESHOLD", "0.50"))
    model_name: str = os.getenv("AI_MODEL_NAME", "aerocompliance-mock")
    model_version: str = os.getenv("AI_MODEL_VERSION", "1.0")
    health_port: int = int(os.getenv("HEALTH_PORT", "8080"))
