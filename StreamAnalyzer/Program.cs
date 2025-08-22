using System.Collections;
using System.Diagnostics.CodeAnalysis;
using LucHeart.StreamAnalyzer;
using LucHeart.StreamAnalyzer.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus.Client.Collectors;
using Prometheus.Client.DependencyInjection;
using Prometheus.Client.MetricPusher;
using Prometheus.Client.MetricPusher.HostedService;
using Rtsp;
using Serilog;

var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

// ReSharper disable once RedundantAssignment
var isDebug = args
    .Any(x => x.Equals("--debug", StringComparison.InvariantCultureIgnoreCase));

#if DEBUG
isDebug = true;
#endif
if (isDebug)
{
    Console.WriteLine("Debug mode enabled");
    loggerConfiguration.MinimumLevel.Verbose();
}

Log.Logger = loggerConfiguration.CreateLogger();

RtspUtils.RegisterUri(); // Registers rtsp uri schema

var builder = Host.CreateDefaultBuilder();
builder.ConfigureHostConfiguration(config =>
{
    config.AddJsonFile("config.json", optional: true, reloadOnChange: false);
    config.AddCommandLine(args);
    config.AddEnvironmentVariables();
});

builder.ConfigureServices((context, services) =>
{
    services.AddSerilog();

    services.AddOptions<Config>().Bind(context.Configuration);

    services.AddMetricFactory();
    
    services.AddSingleton<IMetricPusher, MetricPusher>(provider =>
    {
        var config = provider.GetOptions<Config>();
        return new MetricPusher(new MetricPusherOptions
        {
            Endpoint = config.Prometheus.Endpoint,
            AdditionalHeaders = config.Prometheus.Headers.Select(x => x),
            Job = config.Prometheus.Job,
            CollectorRegistry = provider.GetRequiredService<ICollectorRegistry>()
        });
    });

    services.AddSingleton<IHostedService, MetricPusherHostedService>(provider =>
        new MetricPusherHostedService(provider.GetRequiredService<IMetricPusher>(), TimeSpan.FromSeconds(1)));
    services.AddHostedService<AnalyzerManager>();
});

var app = builder.Build();

await app.RunAsync();