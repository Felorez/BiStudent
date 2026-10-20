using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Firebase;
using BiStudent.Server.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Endpoints;

public static class SyncEndpoints
{
    public static void MapSync(this WebApplication app)
    {
        var group = app.MapGroup("/api/sync").RequireAuthorization()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        group.MapGet("/status", StatusAsync);
        group.MapPost("/run", RunAsync);
    }

    private static async Task<SyncStatusDto> StatusAsync(
        AppDbContext db, SyncService sync, IOptions<FirebaseOptions> options,
        CancellationToken cancellationToken)
    {
        var status = sync.Status;
        return new SyncStatusDto(options.Value.Enabled, status.State.ToString(),
            status.LastSuccessAt, await db.SyncOutbox.CountAsync(cancellationToken),
            status.Error);
    }

    private static async Task<SyncStatusDto> RunAsync(
        AppDbContext db, SyncService sync, IOptions<FirebaseOptions> options,
        CancellationToken cancellationToken)
    {
        await sync.RunAsync(cancellationToken);
        return await StatusAsync(db, sync, options, cancellationToken);
    }
}
