namespace LucHeart.StreamAnalyzer;

public sealed class Config
{
    public Dictionary<string, Uri> Streams { get; set; } = new Dictionary<string, Uri>();
    
    public required PrometheusConfig Prometheus { get; set; }
    
    public sealed class PrometheusConfig
    {
        public required string Endpoint { get; set; }
        public string Job { get; set; } = "rtsp-stream-analyzer";
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }
}