using System.Security.Cryptography;
using System.Text.Json;
using AeroCompliance.ApiService.Authorization;
using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Common;
using AeroCompliance.Contracts.Dtos.Images;
using AeroCompliance.Contracts.Messaging;
using AeroCompliance.Contracts.Messaging.Messages;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Controllers;

[ApiController, Route("api/flights/{flightId:guid}/images"), Authorize]
public sealed class FlightImagesController(AeroComplianceDbContext db, ICurrentUserContext user, IObjectStorage storage,
    IHostEnvironment environment, IConfiguration configuration, IAuditService audit,
    ILogger<FlightImagesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ImageDto>>> List(Guid flightId, int page = 1, int pageSize = 25, string? status = null, CancellationToken ct = default)
    {
        if (!ApiSupport.IsValidPagination(page, pageSize)) return Problem(statusCode: 400, title: "Invalid pagination");
        if (!await VisibleFlights().AnyAsync(x => x.Id == flightId, ct)) return NotFound();
        var q = db.Images.AsNoTracking().Where(x => x.FlightId == flightId);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ImageProcessingStatus>(status, true, out var parsed)) q = q.Where(x => x.ProcessingStatus == parsed);
        return Ok(await q.OrderByDescending(x => x.CreatedAt).Select(x => Map(x)).ToPagedAsync(page, pageSize, ct));
    }

    [HttpPost, Authorize(Policy = Policies.ManageFlights), RequestFormLimits(MultipartBodyLengthLimit = 262_144_000)]
    [RequestSizeLimit(262_144_000)]
    public async Task<ActionResult<ImageReceiptDto>> Upload(Guid flightId, IFormFile file, [FromForm] ImageUploadForm form, CancellationToken ct)
    {
        var flight = await ManageableFlights().SingleOrDefaultAsync(x => x.Id == flightId, ct);
        if (flight is null) return NotFound();
        if (file is null || file.Length <= 0) return Problem(statusCode: 400, title: "An image file is required.");
        var maxBytes = configuration.GetValue<long?>("Uploads:MaxBytes") ?? 262_144_000;
        if (file.Length > maxBytes) return Problem(statusCode: 413, title: "Upload is too large.");
        var demoMode = environment.IsDevelopment() || configuration.GetValue<bool>("DemoMode:Enabled");
        if (!ValidFile(file, demoMode, out var contentType)) return Problem(statusCode: 415, title: "Unsupported image format.");
        if (!ValidScenario(form.MockScenario)) return Problem(statusCode: 400, title: "Invalid mockScenario.");
        if (form.Latitude.HasValue ^ form.Longitude.HasValue) return Problem(statusCode: 400, title: "Latitude and longitude must be supplied together.");
        if (form.Latitude is double lat && (double.IsNaN(lat) || double.IsInfinity(lat))) return Problem(statusCode: 400, title: "Latitude must be a finite number.");
        if (form.Longitude is double lon && (double.IsNaN(lon) || double.IsInfinity(lon))) return Problem(statusCode: 400, title: "Longitude must be a finite number.");

        var imageId = Guid.NewGuid(); var requestId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var original = Sanitize(file.FileName); var ext = Path.GetExtension(original).ToLowerInvariant();
        var key = $"{flight.MunicipalityId:N}/{flightId:N}/{now:yyyy/MM/dd}/{imageId:N}{ext}";
        var temp = Path.Combine(Path.GetTempPath(), $"aerocompliance-{Guid.NewGuid():N}.upload");
        var stored = false;
        try
        {
            string hash;
            using var sha = SHA256.Create();
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            await using (var crypto = new CryptoStream(output, sha, CryptoStreamMode.Write))
            { await file.CopyToAsync(crypto, ct); await crypto.FlushFinalBlockAsync(ct); hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant(); }
            var actualLength = new FileInfo(temp).Length;
            if (actualLength > maxBytes) return Problem(statusCode: 413, title: "Upload is too large.");
            if (!await HasExpectedSignatureAsync(temp, ext, ct))
                return Problem(statusCode: 415, title: "The file content does not match its image format.");

            // Phase 5: extract authoritative EXIF/GPS metadata server-side from the *stored* bytes
            // (never trust a client-side preview). Only JPEG carries EXIF here — GeoTIFF georeferencing
            // is extracted later by the AI Worker via rasterio, since only it has that dependency.
            var exif = ext is ".jpg" or ".jpeg" ? ImageExifExtractor.TryExtract(temp, logger) : null;
            var metadata = ResolveMetadata(form, exif, now);

            await storage.UploadFileAsync(key, temp, contentType, ct); stored = true;
            var message = new ImageProcessingRequested(requestId, 1, requestId, null, now, requestId, imageId, flightId,
                flight.MunicipalityId, storage.Bucket, key, hash, contentType, metadata.CapturedAt, metadata.Latitude, metadata.Longitude,
                demoMode ? form.MockScenario?.Trim().ToLowerInvariant() : null,
                metadata.LocationSource, metadata.CaptureTimeSource);
            var image = new Image { Id = imageId, FlightId = flightId, StoragePath = key, Bucket = storage.Bucket, ObjectKey = key,
                OriginalFileName = original, ContentType = contentType, ByteLength = actualLength, Sha256Hash = hash,
                CapturedAt = metadata.CapturedAt, Latitude = metadata.Latitude, Longitude = metadata.Longitude,
                AltitudeM = form.AltitudeM ?? exif?.AltitudeM, CameraHeadingDeg = form.CameraHeadingDeg ?? exif?.HeadingDeg,
                GpsAccuracyCm = form.GpsAccuracyCm, UploadCompletedAt = now,
                LocationSource = metadata.LocationSource, CaptureTimeSource = metadata.CaptureTimeSource,
                MetadataRequiresReview = metadata.RequiresReview,
                ProcessingStatus = ImageProcessingStatus.Queued, ProcessingRequestId = requestId, AttemptCount = 1, QueuedAt = now, CreatedAt = now };
            db.Images.Add(image);
            db.OutboxMessages.Add(new OutboxMessage { EventId = requestId, Type = nameof(ImageProcessingRequested), RoutingKey = MessagingConstants.ProcessingRequestRoutingKey,
                SchemaVersion = 1, Payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CreatedAt = now });
            audit.Add("image.upload", "Image", imageId, after: new { flightId, originalFileName = original, actualLength, hash, status = "Queued",
                metadata.LocationSource, metadata.CaptureTimeSource, metadata.RequiresReview }, municipalityId: flight.MunicipalityId);
            // SaveChanges is transactional, so the image, outbox row, and audit row commit atomically.
            await db.SaveChangesAsync(ct);
            var receipt = new ImageReceiptDto(imageId, flightId, "Queued", original, actualLength, hash, $"/api/images/{imageId}/processing-status",
                metadata.CapturedAt, metadata.Latitude, metadata.Longitude, metadata.LocationSource, metadata.CaptureTimeSource, metadata.RequiresReview);
            return Accepted(receipt.StatusUrl, receipt);
        }
        catch { if (stored) { try { await storage.DeleteAsync(key, CancellationToken.None); } catch { } } throw; }
        finally
        {
            if (System.IO.File.Exists(temp))
            {
                try { System.IO.File.Delete(temp); }
                catch (IOException) { /* The OS will reclaim an interrupted request's spool file later. */ }
            }
        }
    }

    private IQueryable<Flight> VisibleFlights() => user.IsPlatformAdmin ? db.Flights : db.Flights.Where(x => x.MunicipalityId == user.MunicipalityId);
    private IQueryable<Flight> ManageableFlights() { var q = VisibleFlights(); return user.Role == Roles.DroneOperator ? q.Where(x => x.DroneOperatorId == user.UserId) : q; }
    private static ImageDto Map(Image x) => new(x.Id, x.FlightId, x.OriginalFileName, x.ContentType, x.ByteLength, x.Sha256Hash,
        x.CapturedAt, x.Latitude, x.Longitude, x.AltitudeM, x.CameraHeadingDeg, x.GpsAccuracyCm, x.ProcessingStatus.ToString(), x.AttemptCount, x.UploadCompletedAt, x.CreatedAt,
        x.LocationSource, x.CaptureTimeSource, x.MetadataRequiresReview, x.SourceRasterCrs, x.RasterWidth, x.RasterHeight);
    private static string Sanitize(string name) { var clean = Path.GetFileName(name); foreach (var c in Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '_'); return string.IsNullOrWhiteSpace(clean) ? "image" : clean[..Math.Min(clean.Length, 255)]; }
    private static bool ValidScenario(string? value) => string.IsNullOrWhiteSpace(value) || new[] { "none", "building", "road", "road_encroachment", "parcel_encroachment", "failure" }.Contains(value.Trim().ToLowerInvariant());

    /// <summary>
    /// Phase 5 metadata precedence: valid GeoTIFF georeferencing (discovered later, asynchronously, by
    /// the AI Worker — not resolvable here) &gt; server-extracted EXIF &gt; an explicit user/map value
    /// &gt; missing. <see cref="ImageUploadForm.OverrideLocation"/> lets a caller force its own
    /// coordinates ahead of EXIF for this upload.
    /// </summary>
    private static ResolvedImageMetadata ResolveMetadata(ImageUploadForm form, ExtractedImageMetadata? exif, DateTimeOffset uploadedAt)
    {
        string locationSource;
        double? latitude, longitude;
        if (form.OverrideLocation && form.Latitude.HasValue && form.Longitude.HasValue)
        {
            (latitude, longitude, locationSource) = (form.Latitude, form.Longitude, "UserProvided");
        }
        else if (exif?.Latitude is double exifLat && exif.Longitude is double exifLon)
        {
            (latitude, longitude, locationSource) = (exifLat, exifLon, "Exif");
        }
        else if (form.Latitude.HasValue && form.Longitude.HasValue)
        {
            (latitude, longitude, locationSource) = (form.Latitude, form.Longitude, "UserProvided");
        }
        else
        {
            (latitude, longitude, locationSource) = (null, null, "Missing");
        }

        DateTimeOffset capturedAt; string captureTimeSource;
        if (form.CapturedAt.HasValue) { capturedAt = form.CapturedAt.Value; captureTimeSource = "UserProvided"; }
        else if (exif?.CapturedAt is DateTimeOffset exifCapturedAt) { capturedAt = exifCapturedAt; captureTimeSource = "Exif"; }
        else { capturedAt = uploadedAt; captureTimeSource = "Missing"; } // captured_at is NOT NULL; fall back to upload time.

        var requiresReview = locationSource == "Missing" || captureTimeSource == "Missing";
        return new ResolvedImageMetadata(capturedAt, latitude, longitude, locationSource, captureTimeSource, requiresReview);
    }

    private sealed record ResolvedImageMetadata(DateTimeOffset CapturedAt, double? Latitude, double? Longitude,
        string LocationSource, string CaptureTimeSource, bool RequiresReview);
    private static bool ValidFile(IFormFile file, bool development, out string contentType)
    { var ext = Path.GetExtension(file.FileName).ToLowerInvariant(); var mime = file.ContentType.Trim().ToLowerInvariant(); var tiff = ext is ".tif" or ".tiff" && mime is "image/tiff" or "image/geotiff"; var mock = development && ((ext is ".png" && mime == "image/png") || (ext is ".jpg" or ".jpeg" && mime == "image/jpeg")); contentType = mime; return tiff || mock; }
    private static async Task<bool> HasExpectedSignatureAsync(string path, string extension, CancellationToken ct)
    {
        var header = new byte[8];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8, true);
        var read = await stream.ReadAsync(header, ct);
        return extension switch
        {
            ".png" => read >= 8 && header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".tif" or ".tiff" => read >= 4 &&
                ((header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] is 42 or 43 && header[3] == 0) ||
                 (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0 && header[3] is 42 or 43)),
            _ => false
        };
    }
}
