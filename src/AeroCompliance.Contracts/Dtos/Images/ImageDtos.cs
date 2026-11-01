using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace AeroCompliance.Contracts.Dtos.Images;

public sealed class ImageUploadForm
{
    /// <summary>
    /// Optional as of Phase 5: when omitted, the server falls back to EXIF <c>DateTimeOriginal</c>
    /// (JPEG) or, failing that, the upload time (recorded as <see cref="CaptureTimeSource"/> = "Missing").
    /// </summary>
    public DateTimeOffset? CapturedAt { get; init; }
    [Range(-90, 90)] public double? Latitude { get; init; }
    [Range(-180, 180)] public double? Longitude { get; init; }
    [Range(0, 10000)] public decimal? AltitudeM { get; init; }
    [Range(0, 360)] public decimal? CameraHeadingDeg { get; init; }
    [Range(0, 100000)] public decimal? GpsAccuracyCm { get; init; }
    public string? MockScenario { get; init; }

    /// <summary>
    /// When true, trust this request's <see cref="Latitude"/>/<see cref="Longitude"/> even if the
    /// file's own EXIF GPS tags disagree or are present — an explicit manual/map-picked override
    /// rather than a fallback for a file with no location of its own.
    /// </summary>
    public bool OverrideLocation { get; init; }
}

public sealed record ImageReceiptDto(Guid ImageId, Guid FlightId, string ProcessingStatus,
    string OriginalFileName, long Size, string Sha256, string StatusUrl,
    DateTimeOffset EffectiveCapturedAt, double? EffectiveLatitude, double? EffectiveLongitude,
    string LocationSource, string CaptureTimeSource, bool MetadataRequiresReview);
public sealed record ImageDto(Guid Id, Guid FlightId, string OriginalFileName, string ContentType,
    long ByteLength, string Sha256, DateTimeOffset CapturedAt, double? Latitude, double? Longitude,
    decimal? AltitudeM, decimal? CameraHeadingDeg, decimal? GpsAccuracyCm, string ProcessingStatus,
    int AttemptCount, DateTimeOffset? UploadedAt, DateTimeOffset CreatedAt,
    string LocationSource, string CaptureTimeSource, bool MetadataRequiresReview,
    string? SourceRasterCrs, int? RasterWidth, int? RasterHeight);
public sealed record ImageProcessingStatusDto(Guid ImageId, string Status, Guid? RequestId, int AttemptCount,
    DateTimeOffset? QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string? ErrorCode, string? ErrorMessage,
    // Phase 8 (additive, nullable for images with no analysis yet): what the run actually produced,
    // so the UI can say "51 found, 1 evaluated geographically, 50 retained as model-only" instead of
    // showing a bare detection count that hides the ROI restriction.
    AnalysisSummaryDto? Analysis = null);

/// <summary>
/// Outcome of one image analysis, counted honestly: model-only predictions are real detections the
/// model made, they simply have no trustworthy map position and are never given one.
/// </summary>
public sealed record AnalysisSummaryDto(
    Guid AnalysisId, Guid? RequestId, string Mode, string Status,
    string? ModelName, string? ModelVersion,
    int TotalDetections, int GeographicDetections, int ModelOnlyDetections,
    int OutsideTrustedRoiCount, int CandidateViolationCount,
    string GeographicStatus, string GeographicEvaluationEligibility, string? GeographicSkipReason,
    string? GeoreferencingMethod, string? GeoreferencingConfidence, double? TrustedRoiRadiusM,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

public sealed record DetectionSummaryDto(Guid Id, Guid ImageId, Guid? ImageAnalysisId, int? ClassId,
    string ClassName, decimal Confidence, JsonElement Geometry, DateTimeOffset CreatedAt,
    Guid? FlightId, string? SourceImageFileName, string? FlightMissionName,
    // Phase 8 (additive, null on legacy rows): geometry provenance, so the UI never has to guess
    // from a filename whether a result is real, estimated or synthetic.
    string? ResultSource = null, bool IsApproximate = false, bool RequiresManualReview = false,
    string? GeoreferencingConfidence = null, string? AnalysisMode = null);
public sealed record DetectionDetailsDto(Guid Id, Guid ImageId, Guid? ImageAnalysisId, Guid? ExternalDetectionId,
    int? ClassId, string ClassName, decimal Confidence, JsonElement Geometry, decimal? AreaM2,
    decimal? PerimeterM, decimal? HeightM, string ModelName, string ModelVersion, DateTimeOffset CreatedAt,
    Guid? FlightId, string? SourceImageFileName, string? FlightMissionName,
    string? ResultSource = null, bool IsApproximate = false, bool RequiresManualReview = false,
    string? GeoreferencingConfidence = null, string? SourceGeoreferencingMethod = null,
    double? DistanceFromAnchorM = null, string? AnalysisMode = null, Guid? AnalysisRequestId = null,
    string? GeoreferencingStatus = null, double? TrustedRoiRadiusM = null);
