using PiSharp.WebUi;
using PiSharp.WebUi.App.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddPiSharpCorsProxy(options =>
{
    var configuredTarget = builder.Configuration["PiSharp:CorsProxy:TargetUrl"];
    if (Uri.TryCreate(configuredTarget, UriKind.Absolute, out var targetUrl))
    {
        options.TargetUrl = targetUrl;
    }
});
builder.Services.AddScoped<IChatStorageService, ChatStorageService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UsePiSharpCorsProxy("/api/proxy");
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "PiSharp.WebUi.App",
    utc = DateTimeOffset.UtcNow,
}));

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
