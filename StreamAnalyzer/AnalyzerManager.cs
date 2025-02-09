using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus.Client;

namespace LucHeart.StreamAnalyzer;

public class AnalyzerManager : IHostedService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<Config> _config;
    private readonly ILogger<AnalyzerManager> _logger;
    
    private readonly List<StreamCapture> _streamCaptures = new();
    private readonly IMetricFamily<ICounter<long>,ValueTuple<string>> _streamBitrateMetricVideo;
    private readonly IMetricFamily<ICounter<long>,ValueTuple<string>> _streamBitrateMetricAudio;

    public AnalyzerManager(ILoggerFactory loggerFactory, IOptions<Config> config, IMetricFactory metricFactory)
    {
        _loggerFactory = loggerFactory;
        _config = config;
        _logger = loggerFactory.CreateLogger<AnalyzerManager>();
        
        _streamBitrateMetricVideo = metricFactory.CreateCounterInt64("rtsp_stream_bitrate_video", 
            "The byte rate of the RTSP streams video",
            "stream_name",
            true
            );
        
        _streamBitrateMetricAudio = metricFactory.CreateCounterInt64("rtsp_stream_bitrate_audio", 
            "The byte rate of the RTSP streams audio",
            "stream_name",
            true
        );
    }
    
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, streamUri) in _config.Value.Streams)
        {
            _logger.LogInformation("Starting stream capture for {StreamName} - {StreamUri}", key, streamUri);
            var videoMetric = _streamBitrateMetricVideo.WithLabels(key);
            var audioMetric = _streamBitrateMetricAudio.WithLabels(key);
            _streamCaptures.Add(new StreamCapture(key, streamUri, _loggerFactory, videoMetric, audioMetric));
        }

        await Task.WhenAll(_streamCaptures.Select(x => x.Start()));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeAndClearAllRecorders();
        return Task.CompletedTask;
    }
    
    private void DisposeAndClearAllRecorders()
    {
        foreach (var streamCapture in _streamCaptures)
        {
            try
            {
                streamCapture.Dispose();
            } catch (Exception e)
            {
                _logger.LogError(e, "Error disposing stream capture {StreamName}", streamCapture.ToString());
            }
        }

        _streamCaptures.Clear();
    }
}