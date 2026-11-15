using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Common;

internal sealed class AuditLog(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<AuditLog> logger,
    IHttpContextAccessor? http = null) : IAuditLog
{
    public async Task WriteAsync(string actor, string action, string? targetType = null, string? targetId = null,
        string? details = null, CancellationToken ct = default)
    {
        logger.LogInformation("Audit {Action} by {Actor} on {TargetType} {TargetId}: {Details}", action, actor, targetType, targetId, details);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = time.GetUtcNow().UtcDateTime,
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
            RemoteIp = http?.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
