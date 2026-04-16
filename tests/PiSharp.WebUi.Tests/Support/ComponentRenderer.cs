using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PiSharp.WebUi.Tests.Support;

internal static class ComponentRenderer
{
    public static async Task<string> RenderAsync<TComponent>(
        IDictionary<string, object?>? parameters = null,
        Action<IServiceCollection>? configureServices = null)
        where TComponent : IComponent
    {
        var serviceCollection = new ServiceCollection()
            .AddLogging();

        configureServices?.Invoke(serviceCollection);

        using var services = serviceCollection.BuildServiceProvider();

        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<TComponent>(
                    ParameterView.FromDictionary(parameters ?? new Dictionary<string, object?>()))
                .ConfigureAwait(false);

            return root.ToHtmlString();
        });
    }
}
