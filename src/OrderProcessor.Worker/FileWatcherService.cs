using System.Threading.Channels;

public class FileWatcherService
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly OrderProcessingService _processor;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

    public FileWatcherService(
        ILogger<FileWatcherService> logger,
        OrderProcessingService processor)
    {
        _logger = logger;
        _processor = processor;
    }

    public void Start(CancellationToken token)
    {
        var watcher = new FileSystemWatcher("IncomingOrders", "*.json");
        watcher.Created += (s, e) =>
        {
            _logger.LogInformation("New file detected: {File}", e.FullPath);
            _channel.Writer.TryWrite(e.FullPath);
        };

        watcher.EnableRaisingEvents = true;

        Task.Run(async () =>
        {
            await foreach (var file in _channel.Reader.ReadAllAsync(token))
            {
                await _processor.ProcessAsync(file, token);
            }
        }, token);
    }
}
