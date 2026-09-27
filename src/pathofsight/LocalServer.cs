using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using POE2Radar.Core.Pathfinding;

namespace pathofsight;

public sealed class LocalServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; }
    private LocalServer(WebApplication app, string url) { _app = app; Url = url; }
    public static async Task<LocalServer> Start(MapService service, int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = 65536; });
        var app = builder.Build();
        string? origin = null;
        app.Use(async (context, next) =>
        {
            var expected = origin;
            if (expected == null || context.Request.Host.Value != new Uri(expected).Authority)
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            if (context.Request.Method != "GET" && (context.Request.Headers.Origin != expected
                || context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true))
            { context.Response.StatusCode = 403; return; }
            try { await next(context); }
            catch (Exception e) when (e is JsonException or ArgumentException or BadHttpRequestException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Некорректный запрос" }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "Не удалось сохранить настройки. Проверьте доступ к папке данных." }); }
        });
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pathofsight.dashboard.html")!;
        var html = await new StreamReader(stream).ReadToEndAsync();
        app.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));
        app.MapGet("/api/state", () =>
        {
            var s = service.Snapshot;
            // Clear stale geometry and objectives for the browser, just as for the overlay.
            if (s.Status is "ready" or "demo" && !s.Fresh) s = MapSnapshot.Empty("loading", "Данные обновляются…");
            var view = service.Settings.Read();
            if (s.Instance != view.Instance) s = MapSnapshot.Empty("loading", "Локация обновляется…");
            return Results.Json(new { snapshot = s, settings = view.Settings, profile = view.Profile,
                revision = view.Revision, settingsInstance = view.Instance, temporary = view.Temporary, filters = PoiCatalog.All,
                projection = new { cos = MapProjection.CameraCos, sin = MapProjection.CameraSin } }, SettingsStore.Json);
        });
        app.MapGet("/api/terrain", (string instance) =>
        {
            var s = service.Snapshot;
            return s.Fresh && s.Instance == instance && s.Picture != null ? Results.Bytes(s.Picture.Png, "image/png") : Results.NotFound();
        });
        app.MapPost("/api/target", async (HttpRequest request) =>
        {
            var command = await request.ReadFromJsonAsync<TargetCommand>(SettingsStore.Json);
            return command is { Profile: not null, Revision: not null }
                && service.Select(command.Instance, command.Id, command.Profile, command.Revision)
                ? Results.Ok() : Results.Conflict(new { error = "Локация, профиль или цель уже изменились, либо маршрутизация выключена" });
        });
        app.MapPost("/api/settings", async (HttpRequest request) =>
        {
            var command = await request.ReadFromJsonAsync<SettingsCommand>(SettingsStore.Json);
            if (command is not { Settings: not null, Profile: not null, Instance: not null, Revision: not null }
                || !command.Settings.Valid()) return Results.BadRequest(new { error = "Недопустимые настройки" });
            return service.Settings.WebSave(command.Profile, command.Instance, command.Revision.Value, command.Settings, false)
                ? Results.Ok() : Results.Conflict(new { error = "Настройки изменились. Повторите изменение." });
        });
        app.MapPost("/api/filters", async (HttpRequest request) =>
        {
            var command = await request.ReadFromJsonAsync<SettingsCommand>(SettingsStore.Json);
            if (command is not { Settings: not null, Profile: not null, Instance: not null, Revision: not null }
                || !command.Settings.Valid()) return Results.BadRequest(new { error = "Недопустимый фильтр" });
            return service.Settings.WebSave(command.Profile, command.Instance, command.Revision.Value, command.Settings, true, command.Reset)
                ? Results.Ok() : Results.Conflict(new { error = "Локация или настройки изменились. Повторите изменение." });
        });
        app.MapPost("/api/demo", async (HttpRequest request) =>
        {
            var command = await request.ReadFromJsonAsync<DemoCommand>(SettingsStore.Json);
            if (command is null) return Results.BadRequest();
            if (command.Discover) service.DiscoverDemo(); else service.Demo(command.Enabled);
            return Results.Ok();
        });
        app.MapGet("/api/diagnostics", () => Results.Json(new { product = "Path Of Sight 0.2.1",
            upstream = "Sikaka/POE2Radar@a23cdb6bf8dfb59dee53008a86439f05b24e3a57",
            liveVerified = false, status = service.Snapshot.Status, gameVersion = service.Snapshot.GameVersion,
            terrain = new { service.Snapshot.Width, service.Snapshot.Height }, poiCount = service.Snapshot.Targets.Length,
            warning = service.Settings.LoadWarning }, SettingsStore.Json));
        await app.StartAsync();
        origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
        return new(app, origin);
    }
    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    public record TargetCommand(string Instance, string? Id, string? Profile = null, long? Revision = null);
    public record SettingsCommand(string? Profile, string? Instance, long? Revision, DisplaySettings? Settings, bool Reset = false);
    public record DemoCommand(bool Enabled = false, bool Discover = false);
}
