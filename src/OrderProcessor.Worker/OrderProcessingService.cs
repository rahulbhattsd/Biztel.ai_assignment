using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class OrderProcessingService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderProcessingService> _logger;

    public OrderProcessingService(AppDbContext db, ILogger<OrderProcessingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ProcessAsync(string path, CancellationToken token)
    {
        try
        {
            var json = await ReadFileWithRetry(path);
            var hash = ComputeHash(json);

            if (_db.ProcessedFiles.Any(x => x.Hash == hash))
            {
                _logger.LogInformation("Duplicate file skipped");
                return;
            }

            var order = JsonSerializer.Deserialize<Order>(json);

            if (string.IsNullOrWhiteSpace(order.CustomerName))
                throw new Exception("CustomerName missing");

            if (order.TotalAmount < 0)
                throw new Exception("TotalAmount < 0");

            var valid = new ValidOrder
            {
                OrderId = order.OrderId,
                CustomerName = order.CustomerName,
                OrderDate = order.OrderDate,
                TotalAmount = order.TotalAmount,
                IsHighValue = order.TotalAmount > 1000
            };

            _db.ValidOrders.Add(valid);
            _db.ProcessedFiles.Add(new ProcessedFile { Hash = hash });
            await _db.SaveChangesAsync(token);

            _logger.LogInformation("Order saved: {OrderId}", order.OrderId);
        }
        catch (JsonException)
        {
            SaveInvalid(path, "Corrupted JSON");
        }
        catch (Exception ex)
        {
            SaveInvalid(path, ex.Message);
        }
    }

    private void SaveInvalid(string path, string reason)
    {
        _db.InvalidOrders.Add(new InvalidOrder
        {
            RawJson = File.ReadAllText(path),
            Reason = reason
        });
        _db.SaveChanges();
        _logger.LogWarning("Invalid order saved: {Reason}", reason);
    }

    private async Task<string> ReadFileWithRetry(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
        }
        throw new IOException("File locked");
    }

    private static string ComputeHash(string content)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(content)));
    }
}
