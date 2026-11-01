using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AeroCompliance.ApiService.Services;

/// <summary>
/// Everything the PDF renders, resolved from the database beforehand. The renderer performs no
/// data access and no compliance arithmetic of its own — it prints authoritative stored values.
/// </summary>
public sealed record ViolationReportModel(
    string Reference,
    string MunicipalityName,
    DateTimeOffset GeneratedAt,
    string ViolationReference,
    string ViolationStatusLabel,
    string ViolationType,
    string DetectionClass,
    decimal DetectionConfidence,
    string? FlightMissionName,
    string? SourceImageFileName,
    string? RoadCode,
    string? RoadName,
    string? ParcelNumber,
    string? ZoneNumber,
    string? BlockNumber,
    double? CentroidLatitude,
    double? CentroidLongitude,
    string? EvaluationType,
    decimal? MeasuredValue,
    decimal? RequiredValue,
    decimal? ExceedanceValue,
    decimal? ExceedanceAreaM2,
    string? Unit,
    string? ReviewDecision,
    string? ReviewerName,
    DateTimeOffset? ReviewedAt,
    string? ReviewNotes,
    IReadOnlyList<(string Status, string? Actor, DateTimeOffset At, string? Notes)> History,
    byte[]? EvidenceImage,
    string? EvidenceUnavailableReason,
    bool IsSyntheticDemo = false);

public static class ViolationReportPdf
{
    public const string Disclaimer =
        "AI detections are decision-support evidence. The recorded violation outcome reflects human review in AeroCompliance.";

    private const string Ink = "#0f172a";
    private const string Muted = "#64748b";
    private const string Line = "#e2e8f0";
    private const string Accent = "#1d4ed8";

    public static byte[] Render(ViolationReportModel m) => Document.Create(doc =>
    {
        doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(t => t.FontSize(10).FontColor(Ink).FontFamily(Fonts.Calibri));

            page.Header().Element(e => Header(e, m));
            page.Content().PaddingVertical(14).Element(e => Content(e, m));
            page.Footer().Element(e => Footer(e, m));
        });
    }).GeneratePdf();

    private static void Header(IContainer container, ViolationReportModel m) =>
        container.BorderBottom(1).BorderColor(Line).PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("AeroCompliance").FontSize(17).SemiBold().FontColor(Accent);
                col.Item().Text(m.IsSyntheticDemo ? "SYNTHETIC DEMO REPORT - NOT ENFORCEMENT EVIDENCE"
                    : "Municipal compliance report").FontSize(9).FontColor(Muted);
                col.Item().PaddingTop(4).Text(m.MunicipalityName).FontSize(11).SemiBold();
            });
            row.ConstantItem(190).AlignRight().Column(col =>
            {
                col.Item().Text(m.Reference).FontSize(13).SemiBold();
                col.Item().Text($"Generated {Moment(m.GeneratedAt)}").FontSize(9).FontColor(Muted);
                col.Item().PaddingTop(4).AlignRight()
                    .Background("#eff6ff").Border(1).BorderColor("#bfdbfe").PaddingVertical(3).PaddingHorizontal(7)
                    .Text(m.ViolationStatusLabel).FontSize(9).SemiBold().FontColor(Accent);
            });
        });

    private static void Content(IContainer container, ViolationReportModel m) => container.Column(col =>
    {
        col.Spacing(14);

        col.Item().ShowEntire().Element(e => Section(e, "Violation summary", rows =>
        {
            rows.Add("Violation reference", m.ViolationReference);
            rows.Add("Violation type", Words(m.ViolationType));
            rows.Add("Detection class", Words(m.DetectionClass));
            rows.Add("AI confidence", Percent(m.DetectionConfidence));
            rows.Add("Flight mission", m.FlightMissionName);
            rows.Add("Source image", m.SourceImageFileName);
        }));

        col.Item().ShowEntire().Element(e => Section(e, "Location", rows =>
        {
            rows.Add("Road", Join(m.RoadCode, m.RoadName));
            rows.Add("Parcel number", m.ParcelNumber);
            rows.Add("Zone number", m.ZoneNumber);
            rows.Add("Block number", m.BlockNumber);
            rows.Add("Approximate centre",
                m.CentroidLatitude is double lat && m.CentroidLongitude is double lon
                    ? $"{lat:0.00000}, {lon:0.00000} (latitude, longitude)"
                    : null);
        }));

        col.Item().ShowEntire().Element(e => Section(e, "Compliance evaluation", rows =>
        {
            rows.Add("Evaluation type", Words(m.EvaluationType));
            rows.Add("Measured value", Measure(m.MeasuredValue, m.Unit));
            rows.Add("Required value", Measure(m.RequiredValue, m.Unit));
            rows.Add("Exceedance", Measure(m.ExceedanceValue, m.Unit));
            rows.Add("Encroachment area", m.ExceedanceAreaM2 is decimal a ? $"{a:0.##} m²" : null);
            rows.Add("AI confidence", Percent(m.DetectionConfidence));
        }));

        col.Item().ShowEntire().Element(e => Section(e, m.IsSyntheticDemo ? "Demo review" : "Human review", rows =>
        {
            rows.Add("Decision", m.ReviewDecision);
            rows.Add("Reviewer", m.ReviewerName);
            rows.Add("Reviewed at", m.ReviewedAt is DateTimeOffset at ? Moment(at) : null);
            rows.Add("Notes", m.ReviewNotes);
        }));

        if (m.History.Count > 0)
            col.Item().Element(e => History(e, m));

        // Keep each of these together: a heading stranded at the foot of a page with its
        // content overleaf reads as a formatting fault in an official document.
        col.Item().ShowEntire().Element(e => Evidence(e, m));

        col.Item().ShowEntire().Background("#f8fafc").Border(1).BorderColor(Line).Padding(10)
            .Text(m.IsSyntheticDemo
                ? "Synthetic demonstration only. Imagery, GIS geometry, AI detections, review and locations are fictional or mock outputs. This document does not establish real AI accuracy or a real compliance finding."
                : Disclaimer).FontSize(9).Italic().FontColor(Muted);
    });

    private static void History(IContainer container, ViolationReportModel m) =>
        container.Column(col =>
        {
            col.Item().Element(Heading).Text("Status history");
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c => { c.ConstantColumn(120); c.ConstantColumn(150); c.RelativeColumn(); });
                foreach (var header in new[] { "Date", "Status", "Notes" })
                    table.Cell().Element(TableHeader).Text(header).FontSize(9).SemiBold().FontColor(Muted);
                foreach (var (status, actor, at, notes) in m.History)
                {
                    table.Cell().Element(TableCell).Text(Moment(at)).FontSize(9);
                    table.Cell().Element(TableCell).Text(Words(status)).FontSize(9);
                    table.Cell().Element(TableCell).Column(c =>
                    {
                        c.Item().Text(string.IsNullOrWhiteSpace(notes) ? "—" : notes!).FontSize(9);
                        if (!string.IsNullOrWhiteSpace(actor)) c.Item().Text(actor!).FontSize(8).FontColor(Muted);
                    });
                }
            });
        });

    private static void Evidence(IContainer container, ViolationReportModel m) =>
        container.Column(col =>
        {
            col.Item().Element(Heading).Text("Evidence");
            if (m.EvidenceImage is { Length: > 0 })
            {
                col.Item().PaddingTop(6).MaxHeight(240).AlignLeft()
                    .Image(m.EvidenceImage).FitArea();
                if (!string.IsNullOrWhiteSpace(m.SourceImageFileName))
                    col.Item().PaddingTop(4).Text($"Source image: {m.SourceImageFileName}").FontSize(9).FontColor(Muted);
            }
            else
            {
                col.Item().PaddingTop(6).Text(m.EvidenceUnavailableReason
                    ?? "No preview is available for this evidence image.").FontSize(9).FontColor(Muted);
            }
        });

    private static void Section(IContainer container, string title, Action<FieldRows> build)
    {
        var rows = new FieldRows();
        build(rows);
        container.Column(col =>
        {
            col.Item().Element(Heading).Text(title);
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c => { c.ConstantColumn(150); c.RelativeColumn(); });
                foreach (var (label, value) in rows.Items)
                {
                    table.Cell().Element(TableCell).Text(label).FontSize(9).FontColor(Muted);
                    table.Cell().Element(TableCell).Text(value ?? "Not recorded").FontSize(10)
                        .FontColor(value is null ? Muted : Ink);
                }
            });
        });
    }

    private static void Footer(IContainer container, ViolationReportModel m) =>
        container.BorderTop(1).BorderColor(Line).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text($"{m.Reference} · {m.MunicipalityName}").FontSize(8).FontColor(Muted);
            row.ConstantItem(120).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
            });
        });

    private static IContainer Heading(IContainer c) => c.BorderBottom(1).BorderColor(Line).PaddingBottom(3);
    private static IContainer TableHeader(IContainer c) => c.BorderBottom(1).BorderColor(Line).PaddingVertical(4).PaddingRight(8);
    private static IContainer TableCell(IContainer c) => c.PaddingVertical(3).PaddingRight(8);

    /// <summary>Collects label/value pairs, skipping nothing so absent data reads as "Not recorded".</summary>
    private sealed class FieldRows
    {
        public List<(string Label, string? Value)> Items { get; } = [];
        public void Add(string label, string? value) => Items.Add((label, string.IsNullOrWhiteSpace(value) ? null : value.Trim()));
    }

    private static string Moment(DateTimeOffset value) => $"{value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC";
    private static string Percent(decimal value) => $"{value * 100m:0.#}%";
    private static string? Words(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Replace('_', ' ');
    private static string? Measure(decimal? value, string? unit) =>
        value is null ? null : unit is null or "" ? $"{value:0.##}" : $"{value:0.##} {unit}";
    private static string? Join(string? code, string? name) =>
        string.IsNullOrWhiteSpace(code) ? name : string.IsNullOrWhiteSpace(name) ? code : $"{code} — {name}";
}
