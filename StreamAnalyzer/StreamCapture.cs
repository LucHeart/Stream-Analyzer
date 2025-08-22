using Microsoft.Extensions.Logging;
using Prometheus.Client;
using RtspClientExample;

namespace LucHeart.StreamAnalyzer;

public class StreamCapture : IDisposable
{
    private readonly string _name;
    private readonly Uri _uri;
    private readonly ICounter<long> _videoMetric;
    private readonly ICounter<long> _audioMetric;
    private readonly ILogger<StreamCapture> _logger;
    private readonly RtspClient _rtspClient;
    
    
    public StreamCapture(string name, Uri uri, ILoggerFactory loggerFactory, ICounter<long> videoMetric, ICounter<long> audioMetric)
    {
        _name = name;
        _uri = uri;
        _videoMetric = videoMetric;
        _audioMetric = audioMetric;
        _logger = loggerFactory.CreateLogger<StreamCapture>();
        _rtspClient = new RtspClient(loggerFactory);
        
        _rtspClient.SetupMessageCompleted += (_, _) =>
        {
            _rtspClient.Play();
        };

        _rtspClient.ReceivedAudioData += RtspClientOnReceivedAudioData;
        
        _rtspClient.ReceivedVideoData +=  RtspClientOnReceivedVideoData;
    }

    private void RtspClientOnReceivedVideoData(object? sender, SimpleDataEventArgs e)
    {
        foreach (var readOnlyMemory in e.Data)
        {
            _videoMetric.Inc(readOnlyMemory.Length, DateTimeOffset.UtcNow);
        }
    }

    private void RtspClientOnReceivedAudioData(object? sender, SimpleDataEventArgs e)
    {
        foreach (var readOnlyMemory in e.Data)
        {
            _audioMetric.Inc(readOnlyMemory.Length, DateTimeOffset.UtcNow);
        }
    }

    public async Task Start()
    {
        _rtspClient.Connect(_uri);
    }


    private bool _disposed;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _rtspClient.ReceivedAudioData -= RtspClientOnReceivedAudioData;
        _rtspClient.ReceivedVideoData -= RtspClientOnReceivedVideoData;
        
        _rtspClient.Dispose();
        
        GC.SuppressFinalize(this);
    }

    public override string ToString()
    {
        return $"{nameof(StreamCapture)} {_name} - {_uri}";
    }
}