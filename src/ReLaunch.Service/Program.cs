using ReLaunch.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = ProductInfo.ServiceName);
builder.WebHost.UseUrls($"http://127.0.0.1:{ProductInfo.DebugApiPort}");

var app = builder.Build();
app.MapGet("/", () => $"{ProductInfo.Name} service");
app.Run();
