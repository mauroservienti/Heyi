namespace Heyi.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus.TransactionalSession;

sealed class TestApp : IAsyncDisposable
{
    readonly WebApplication app;

    TestApp(WebApplication app, FakeTransactionalSession session)
    {
        this.app = app;
        Session = session;
    }

    public FakeTransactionalSession Session { get; }

    public static async Task<TestApp> Start(
        Action<IEndpointRouteBuilder> map,
        Action<HeyiOptions>? configureHeyi = null,
        bool addHeyi = true,
        bool addTransactionalSession = true)
    {
        var session = new FakeTransactionalSession();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        if (addTransactionalSession)
        {
            builder.Services.AddScoped<ITransactionalSession>(_ => session);
        }

        if (addHeyi)
        {
            builder.Services.AddHeyi(configureHeyi ?? (options => options.OpenSessionOptions(() => new TestOpenSessionOptions())));
        }

        var app = builder.Build();
        map(app);
        await app.StartAsync();

        return new TestApp(app, session);
    }

    public HttpClient CreateClient() => app.GetTestClient();

    public void BuildEndpoints()
    {
        foreach (var dataSource in ((IEndpointRouteBuilder)app).DataSources)
        {
            _ = dataSource.Endpoints;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
