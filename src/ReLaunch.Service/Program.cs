using Microsoft.AspNetCore.Builder;
using ReLaunch.Core;
using ReLaunch.Core.Actions;
using ReLaunch.Service;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var settings = ServiceSettings.Load();
var debugApi = settings.DebugApi || Environment.GetEnvironmentVariable("RELAUNCH_DEBUG_API") == "1";

if (debugApi)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddWindowsService(o => o.ServiceName = ProductInfo.ServiceName);
    builder.Services.AddReLaunch();
    builder.WebHost.UseUrls($"http://127.0.0.1:{ProductInfo.DebugApiPort}");
    var app = builder.Build();
    DebugApi.Map(app);
    app.Run();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(o => o.ServiceName = ProductInfo.ServiceName);
    builder.Services.AddReLaunch();
    builder.Build().Run();
}

internal static class ServiceRegistration
{
    public static IServiceCollection AddReLaunch(this IServiceCollection s)
    {
        s.AddSingleton<Database>();
        s.AddSingleton<EventHub>();
        s.AddSingleton<AgentClient>();
        s.AddSingleton<ProcessSnapshotCache>();
        s.AddSingleton<IAppRuntime, AgentAppRuntime>();
        s.AddSingleton<AppActionRunner>();
        s.AddSingleton<ControlService>();

        s.AddSingleton<AgentSupervisor>();
        s.AddHostedService(sp => sp.GetRequiredService<AgentSupervisor>());
        s.AddSingleton<CatalogueService>();
        s.AddHostedService(sp => sp.GetRequiredService<CatalogueService>());
        s.AddHostedService<StatusPublisher>();
        return s;
    }
}
