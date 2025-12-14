# High-Performance Order Processor

A .NET 6+ Worker Service that ingests legacy order JSON files dropped into an `IncomingOrders` folder, validates and classifies them, and persists results into SQLite. A companion order generator tool produces test files (valid, invalid, corrupted, bursts). The project runs as a console app during development and can be installed as a Windows Service.

---

## Repository layout

- src/OrderProcessor.Worker/ — Worker service project
  - FileWatcherService.cs — uses FileSystemWatcher to detect new files
  - OrderProcessingService.cs — parsing, validation, business rules, persistence
  - Data/AppDbContext.cs — EF Core SQLite DbContext
  - Models/ — Order, ValidOrder, InvalidOrder, ProcessedFile
  - IncomingOrders/ — drop-in folder used by default
- src/OrderGenerator.Tool/ — CLI tool to generate test JSON files
- tests/OrderProcessor.Tests/ — Automated tests (including IdempotencyTests)
- README.md — this file

---

## Prerequisites

- .NET 6 SDK or newer
- Windows (for Windows Service instructions). The worker runs as a console app cross-platform.
- Optional: sqlite3 or DB Browser for SQLite to inspect the DB file

---

## Quick start — run in console (development)

1. Clone the repo
   ```bash
   git clone https://github.com/<your-org>/HighPerformanceOrderProcessor.git
   cd HighPerformanceOrderProcessor
   ```

2. Run the Worker in console mode (shows logs in terminal)
   ```bash
   dotnet run --project src/OrderProcessor.Worker
   ```

   The service will:
   - Ensure the IncomingOrders folder exists (default: `src/OrderProcessor.Worker/IncomingOrders`)
   - Start a FileSystemWatcher and process new `.json` files as they appear
   - Persist to SQLite database at `data/orders.db` (relative to worker project) by default

3. Run the Order Generator to produce 1000 sample orders (example)
   ```bash
   dotnet run --project src/OrderGenerator.Tool -- 1000
   ```

4. Run automated tests
   ```bash
   dotnet test
   ```

---

## Windows Service installation

1. Publish the worker:
   ```powershell
   dotnet publish src/OrderProcessor.Worker -c Release -o ./publish
   ```

2. Install the service (run as Administrator). Use the full path to published exe:
   ```powershell
   sc create OrderProcessor binPath= "C:\path\to\HighPerformanceOrderProcessor\src\OrderProcessor.Worker\publish\OrderProcessor.Worker.exe" start= auto
   sc description OrderProcessor "High-Performance Order Processor - watches IncomingOrders and saves to SQLite"
   sc start OrderProcessor
   ```

3. Stop and remove:
   ```powershell
   sc stop OrderProcessor
   sc delete OrderProcessor
   ```

Notes:
- If you publish framework-dependent, ensure target machine has required .NET runtime.
- For production service installs, consider NSSM or a Windows service wrapper for better stdout/stderr management.

---

## Configuration

The worker reads configuration from `appsettings.json` and environment variables. Common settings:

- Incoming folder path: `IncomingOrders` (env: `WORKER_INCOMING_DIR`)
- DB file: `data/orders.db` (env: `WORKER_DB_PATH`)
- Logging level and sinks are configurable via Serilog settings

Environment variables override `appsettings.json` values.

---

## How it works (architecture & design)

1. FileWatcherService
   - Uses `FileSystemWatcher` to receive Created/Changed events for `*.json`.
   - Enqueues detected file paths into a processing queue.
   - Debounces rapid duplicate events and verifies file readability before processing.

2. File ingestion pipeline
   - Event-driven (FileSystemWatcher) — polling not used.
   - When a new file is detected, worker attempts to open it with retries/backoff to handle files still being written/locked.
   - Computes SHA256 hash of file content and checks `ProcessedFiles` table (UNIQUE constraint). If hash exists, file is skipped (idempotency).
   - Processing and persistence are done transactionally where possible.

3. Order processing & business rules
   - JSON parsed into `Order` model:
     - OrderId (GUID)
     - CustomerName (string)
     - OrderDate (UTC)
     - Items (array)
     - TotalAmount (decimal)
   - Validation rules:
     - Invalid if `TotalAmount < 0`
     - Invalid if `CustomerName` is missing or empty
   - Business rule:
     - If `TotalAmount > 1000` → mark order as `HighValue`
   - Valid orders inserted into `ValidOrders`.
   - Invalid orders (including parse errors) inserted into `InvalidOrders` with `FailureReason`.
   - File hash inserted into `ProcessedFiles` to prevent reprocessing.

4. Persistence (SQLite)
   - Tables:
     - ValidOrders: stores parsed order fields + IsHighValue + timestamps
     - InvalidOrders: stores raw content + FailureReason + timestamps
     - ProcessedFiles: stores FileName, FileHash (SHA256, UNIQUE), Outcome, ProcessedAt
   - `UNIQUE(FileHash)` ensures idempotency by content.

---

## Idempotency (explicit)

- Each file's full content is hashed using SHA256.
- `ProcessedFiles.FileHash` has a UNIQUE constraint.
- Before processing a file, the service computes the hash; if it exists in the DB the file is skipped.
- The hash is recorded in the same transaction as the order insert, preventing double-processing on crashes.

This handles:
- Duplicate filenames with identical content
- Re-delivery of same file
- Service restarts and retries

---

## Order generator (src/OrderGenerator.Tool)

This CLI creates JSON files in the IncomingOrders folder to test various conditions.

Usage examples:

- Generate 100 valid orders:
  ```bash
  dotnet run --project src/OrderGenerator.Tool -- 100
  ```

- Generate 1000 files quickly (burst):
  ```bash
  dotnet run --project src/OrderGenerator.Tool -- 1000 --delay 0
  ```

- Generate 200 files, 20% invalid, 10% corrupted:
  ```bash
  dotnet run --project src/OrderGenerator.Tool -- 200 --invalid-rate 0.2 --corrupt-rate 0.1
  ```

- CLI flags:
  - `<count>` (positional): number of files to generate
  - `--delay <ms>`: delay between file creations (default 10ms)
  - `--invalid-rate <0..1>`: fraction of files that are invalid
  - `--corrupt-rate <0..1>`: fraction of files that are malformed
  - `--output-dir <path>`: write files to this directory (defaults to worker IncomingOrders)
  - `--hold <ms>`: create a file and keep it locked for `<ms>` to simulate locked-writing scenarios

The generator is standalone and configurable via CLI arguments for quick evaluator tuning.

---

## Logging & resiliency

- Uses structured logging (ILogger + Serilog).
- Logged events include:
  - New file detected
  - File open/lock retry attempts
  - Processing started
  - Validation failure and reasons
  - Order saved (valid/invalid)
  - Exceptions
- The worker catches per-file exceptions and continues running. It is designed to stay alive during:
  - File locks
  - DB busy states
  - Corrupted JSON
  - High input spikes

---

## Testing

Automated tests are under `tests/OrderProcessor.Tests`. At minimum there is an `IdempotencyTests.cs` that verifies the same file processed twice does not create duplicate DB records.

Run all tests:
```bash
dotnet test
```

Provided test scenarios (how to repeat):

1. High-volume bursts
   - Start worker:
     ```bash
     dotnet run --project src/OrderProcessor.Worker
     ```
   - Generate burst:
     ```bash
     dotnet run --project src/OrderGenerator.Tool -- 1000 --delay 0
     ```
   - Observe worker logs and DB contents.

2. Locked files
   - Use generator with `--hold` to lock files:
     ```bash
     dotnet run --project src/OrderGenerator.Tool -- 10 --hold 5000
     ```
   - Or manually lock a file via PowerShell:
     ```powershell
     $fs = [System.IO.File]::Open("src\OrderProcessor.Worker\IncomingOrders\locked.json", 'Open', 'ReadWrite', 'None')
     # Keep $fs open to hold lock, then release by closing the handle
     ```

3. Corrupted JSON
   - Generate some corrupted files:
     ```bash
     dotnet run --project src/OrderGenerator.Tool -- 50 --corrupt-rate 0.5
     ```
   - Worker will log parse errors and insert into `InvalidOrders`.

4. Invalid orders
   - Generate invalid orders:
     ```bash
     dotnet run --project src/OrderGenerator.Tool -- 50 --invalid-rate 1.0
     ```

5. Idempotency
   - Copy a processed file's content into a new file with different name and drop it into IncomingOrders. Worker will compute hash and skip if already processed.
   - Run the Idempotency automated test:
     ```bash
     dotnet test --filter Idempotency
     ```

---

## Sample JSON files

Valid order:
```json
{
  "OrderId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "CustomerName": "Jane Doe",
  "OrderDate": "2024-11-01T12:00:00Z",
  "Items": [
    { "Sku": "ABC123", "Quantity": 2, "UnitPrice": 25.5 }
  ],
  "TotalAmount": 51.0
}
```

High-value order (TotalAmount > 1000):
```json
{
  "OrderId": "b3d9c1e5-6a01-4d1d-9f8f-1a2b3c4d5e6f",
  "CustomerName": "Acme Corp",
  "OrderDate": "2024-11-01T12:00:00Z",
  "Items": [
    { "Sku": "BIGITEM", "Quantity": 1, "UnitPrice": 1500.0 }
  ],
  "TotalAmount": 1500.0
}
```

Invalid order (negative total):
```json
{
  "OrderId": "f7e8d9c0-1111-2222-3333-444455556666",
  "CustomerName": "John Smith",
  "OrderDate": "2024-11-01T12:00:00Z",
  "Items": [],
  "TotalAmount": -20.0
}
```

Corrupted JSON (malformed):
```json
{ "OrderId": "bad-json", "CustomerName": "Broken
```

---

## Database inspection

Default DB path:
```
src/OrderProcessor.Worker/data/orders.db
```

Inspect with sqlite3:
```bash
sqlite3 src/OrderProcessor.Worker/data/orders.db
sqlite> .tables
sqlite> select * from ValidOrders limit 10;
```

---

## Design decisions & tradeoffs

- FileSystemWatcher chosen because the assignment forbids polling and it's efficient for local filesystems. It can miss events under extreme load; mitigations include:
  - Debounce/verification step
  - Using file content hashing + DB uniqueness for idempotency
- Idempotency is implemented using content hashing (SHA256). This is robust to filename changes and duplicates.
- SQLite chosen for simplicity. For scale, replace with central RDBMS and a message queue.
- The design prioritizes per-file resilience and survivability: retries on IO, bounded worker queue, per-file exception handling.

---

## One improvement with more time

Replace FileSystemWatcher with a message queue (RabbitMQ / Kafka / Azure Service Bus) and a small forwarder on the legacy system that publishes a message after file write completes. Benefits:
- Reliable delivery, DLQs, retries
- Horizontal scaling
- Backpressure and monitoring

---

## Troubleshooting & FAQs

- "Service isn't processing files": verify IncomingOrders config path, permissions, and check logs.
- "Files processed multiple times": ensure `ProcessedFiles` table exists and DB file is persistent between runs.
- "Cannot install service": confirm exe path and run installer commands as Administrator.

---

## Contact

Maintainer: rahulbhattsd (GitHub)  
Open an issue with logs and details if you encounter problems.

---

## License

MIT

Thank you — drop files into the IncomingOrders folder and watch orders flow into the DB. Happy testing!
 
 




