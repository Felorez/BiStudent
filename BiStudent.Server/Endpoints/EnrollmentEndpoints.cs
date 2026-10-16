using System.Security.Claims;
using BiStudent.Contracts;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BiStudent.Server.Endpoints;

public static class EnrollmentEndpoints
{
    public static void MapEnrollment(this WebApplication app)
    {
        var publicApi = app.MapGroup("/api/public/enrollment").AllowAnonymous()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        publicApi.MapPost("/check", (PublicCodeRequest request, EnrollmentService service) =>
            service.CheckAsync(request.Code)).RequireRateLimiting("public-check");
        publicApi.MapPost("/submit", (PublicSubmitRequest request, EnrollmentService service) =>
            service.SubmitAsync(request)).RequireRateLimiting("public-submit")
            .WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024));

        var groups = app.MapGroup("/api/groups").RequireAuthorization()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        groups.MapGet("/{id:int}/enrollment-code", async (int id, HttpContext http, EnrollmentService service) =>
        {
            var code = await service.GetActiveAsync(id);
            return code is null ? Results.NoContent() : Results.Ok(service.ToDto(code, http));
        });
        groups.MapPost("/{id:int}/enrollment-code", async (int id, CreateEnrollmentCodeRequest request,
            ClaimsPrincipal user, HttpContext http, EnrollmentService service) =>
            service.ToDto(await service.CreateAsync(ApiEndpoints.TeacherId(user), id, request.Hours), http));
        groups.MapDelete("/{id:int}/enrollment-code", async (int id, EnrollmentService service) =>
        {
            await service.RevokeAsync(id);
            return Results.NoContent();
        });

        var submissions = app.MapGroup("/api/face-submissions").RequireAuthorization()
            .AddEndpointFilter(ApiEndpoints.HandleErrorsAsync);
        submissions.MapGet("", (FaceSubmissionStatus? status, EnrollmentService service) =>
            service.ListAsync(status));
        submissions.MapGet("/{id:int}/images/{index:int}", async (int id, int index, EnrollmentService service) =>
            Results.File(await service.ImageAsync(id, index), "image/jpeg"));
        submissions.MapPost("/{id:int}/approve", async (int id, ClaimsPrincipal user, EnrollmentService service) =>
        {
            await service.ApproveAsync(ApiEndpoints.TeacherId(user), id);
            return Results.NoContent();
        });
        submissions.MapPost("/{id:int}/reject", async (int id, RejectSubmissionRequest request,
            ClaimsPrincipal user, EnrollmentService service) =>
        {
            await service.RejectAsync(ApiEndpoints.TeacherId(user), id, request.Reason);
            return Results.NoContent();
        });
    }
}
