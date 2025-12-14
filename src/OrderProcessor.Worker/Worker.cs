public class Worker : BackgroundService
{
    private readonly FileWatcherService _watcher;

    public Worker(FileWatcherService watcher)
    {
        _watcher = watcher;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.Start(stoppingToken);
        return Task.CompletedTask;
    }
}
