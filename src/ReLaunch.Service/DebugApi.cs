using Microsoft.AspNetCore.Mvc;
using ReLaunch.Core;
using ReLaunch.Core.Actions;
using ReLaunch.Core.Catalogue;

namespace ReLaunch.Service;

/// <summary>Optional loopback HTTP API (127.0.0.1:5480) for debugging with curl. Off unless enabled in settings.</summary>
public static class DebugApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => $"{ProductInfo.Name} debug API");

        app.MapGet("/api/status", (ControlService c) => c.GetStatus());

        app.MapGet("/api/apps", async (ControlService c, bool? favorites, string? q, CancellationToken ct) =>
            await c.GetAppsAsync(favorites == true, q, ct));

        app.MapGet("/api/apps/{id}/icon", (ControlService c, string id) =>
            c.GetIcon(id) is { } png ? Results.File(png, "image/png") : Results.NotFound());

        app.MapPost("/api/apps/{id}/{action}", async (ControlService c, string id, string action, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ActionKind>(action, ignoreCase: true, out var kind)) return Results.NotFound();
            var result = await c.ActAsync(id, kind, "debug", ct);
            return Results.Json(result, statusCode: result.Ok ? 200 : result.ErrorCode == "busy" ? 409 : 422);
        });

        app.MapPut("/api/apps/{id}/override", (Database db, CatalogueService cat, string id, OverrideBody body) =>
        {
            db.SetOverride(new AppOverride(id, body.Args, body.ExtraProcesses ?? [], body.CloseTimeoutMs));
            cat.ApplyOverrides();
            return Results.NoContent();
        });

        app.MapPut("/api/favorites", (ControlService c, [FromBody] string[] ids) =>
        {
            c.SetFavorites(ids);
            return Results.NoContent();
        });

        app.MapGet("/api/activity", (ControlService c, int? limit) => c.GetActivity(limit ?? 50));

        app.MapPost("/api/catalogue/refresh", (CatalogueService cat) =>
        {
            cat.RequestRefresh();
            return Results.Accepted();
        });

        app.MapGet("/api/events", async (HttpContext ctx, EventHub hub) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            try
            {
                await foreach (var msg in hub.SubscribeAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"data: {msg}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public sealed record OverrideBody(string? Args, string[]? ExtraProcesses, int? CloseTimeoutMs);
}
