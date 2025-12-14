using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton<FileWatcherService>();
        services.AddSingleton<OrderProcessingService>();
        services.AddDbContext<AppDbContext>();
    })
    .Build()
    .Run();
