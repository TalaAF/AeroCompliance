using AeroCompliance.Infrastructure.Persistence;
using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AeroCompliance.ApiService.Services;

/// <summary>Transaction-scoped PostgreSQL lock for one violation across all API instances.</summary>
public static class ReportGenerationGate
{
    public static async Task<bool> TryAcquireAsync(AeroComplianceDbContext db, Guid violationId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(@lock_key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        // A 64-bit advisory key is stable across processes. A rare collision only serializes
        // unrelated reports; it cannot allow duplicate generation of the same violation.
        parameter.Value = BinaryPrimitives.ReadInt64BigEndian(violationId.ToByteArray());
        command.Parameters.Add(parameter);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }
}
