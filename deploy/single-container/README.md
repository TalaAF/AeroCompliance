# AeroCompliance single-container demo

After the multi-architecture image is published, the complete local demo
stack needs only Docker:

```sh
docker pull tala211/aerocompliance:latest
docker run -d --name aerocompliance --restart unless-stopped \
  -p 8080:8080 \
  -v aerocompliance-data:/var/lib/aerocompliance \
  tala211/aerocompliance:latest
docker logs aerocompliance
```

Open http://localhost:8080. The first startup generates and persists settings under
`/var/lib/aerocompliance`; `docker logs aerocompliance` prints the generated demo login. Stop and
restart without losing data using `docker stop aerocompliance` and `docker start aerocompliance`.

The bundled nginx proxies to the API over `127.0.0.1`, which the API trusts as its local proxy.
Nginx replaces inbound `X-Forwarded-For` with the connecting peer's address, so a client cannot
spoof its audit IP or IP-based login limit through this endpoint. If a separate public reverse
proxy is added in front, the recorded address is that proxy unless a separate trusted ingress
policy is configured.

On a **new empty volume**, wait for Docker to report `healthy`. First startup then contains
four fictional aerial images, three completed sample flights, a planned follow-up flight,
mock building and road detections, two candidates awaiting review, and a labelled sample
PDF report. The images and coordinates are synthetic examples, not municipal source data
or evidence of AI accuracy. Review the pending candidates in the frontend to practice
confirmation or dismissal; the sample report demonstrates the normal report workflow.
The first-start marker is stored in the same data volume, so restarting or updating the
image preserves credentials, reviews, reports, and other user data. Existing volumes are
not backfilled with this scenario.

The default AI mode for new jobs is **YOLO**. The image includes the project checkpoint at
`/opt/aerocompliance/models/best.pt` (SHA-256
`577ec6f619b5e22a462b0ac62d5a55722cded832ea07fbaf70f01d93786479f8`). No extra model
mount or environment setting is needed. A new volume first initializes its explicitly synthetic
scenario using a temporary mock worker; the public endpoint opens only after that finishes and
the selected worker is ready. Those historical results remain labelled `MockGeographicScenario`.
They are never attributed to YOLO or used as accuracy evidence. YOLO predictions from ordinary
PNG/JPEG images without a raster georeference remain model-only; they cannot create geographic
compliance candidates from invented coordinates.
The checkpoint remains Git-ignored; rebuilding the image from source requires the authorized
`src/AeroCompliance.AiWorker/models/best.pt` file with the SHA-256 above in the build context.

To start in mock mode, add `-e AEROCOMPLIANCE_AI_MODE=mock` to `docker run`. Only `yolo` and `mock`
are valid. The configured and active modes are printed in `docker logs`. **Switching modes requires
recreating the container with the same named volume**; a restart of the same container keeps its
original environment. For example, after a run using the volume `aerocompliance-data`:

```sh
docker stop aerocompliance
docker rm aerocompliance
docker run -d --name aerocompliance --restart unless-stopped \
  -p 8080:8080 \
  -v aerocompliance-data:/var/lib/aerocompliance \
  -e AEROCOMPLIANCE_AI_MODE=mock \
  tala211/aerocompliance:latest
```

This keeps credentials, flights, reports, reviews, and old detection provenance. To switch back,
recreate the container again with the same volume and omit the `-e` option. The build below
publishes `2026.10.14-yolo-default-multiarch` for Linux AMD64 and ARM64, then promotes it to
`latest` after release checks. It does not modify older fixed tags.

## Build and publish the multi-architecture image

Run from the repository root with Docker Buildx and Docker Hub login configured:

```sh
docker buildx create --name aerocompliance-multiarch --driver docker-container --use
docker buildx inspect --bootstrap
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t tala211/aerocompliance:2026.10.14-yolo-default-multiarch \
  --push .
docker buildx imagetools inspect tala211/aerocompliance:2026.10.14-yolo-default-multiarch
```

The inspection must list `linux/amd64` and `linux/arm64`. Buildx may add
`unknown/unknown` attestation entries; these are metadata, not extra runtime
architectures. Test the versioned image on native ARM64 with a fresh volume,
including review and PDF download, before promoting it to `latest`:

```sh
docker buildx imagetools create \
  -t tala211/aerocompliance:latest \
  tala211/aerocompliance:2026.10.14-yolo-default-multiarch
docker buildx imagetools inspect tala211/aerocompliance:latest
```

Do not retag or push `2026.10.09-demo-amd64`.

Smoke test the published image on an AMD64 or ARM64 Docker host:

```sh
docker pull tala211/aerocompliance:latest
docker run -d --name aerocompliance-smoke -p 8080:8080 \
  -v aerocompliance-smoke-data:/var/lib/aerocompliance \
  tala211/aerocompliance:latest
docker inspect --format '{{.State.Health.Status}}' aerocompliance-smoke
curl --fail http://localhost:8080/health
curl --fail http://localhost:8080/
docker logs aerocompliance-smoke
```

Wait for Docker to report `healthy` before checking the application. On an
ARM64 host, this smoke test exercises the ARM64 image automatically, without
`--platform`. Stop and remove the smoke container when finished with
`docker rm -f aerocompliance-smoke`; the named data volume is retained.
