High-Performance Order Processor
A .NET 6+ Worker Service that ingests legacy order JSON files dropped into an IncomingOrders folder, validates and classifies them, and persists results into SQLite. A companion order generator tool produces test files (valid, invalid, corrupted, bursts). The project is intentionally simple to run as a console app during development and can be installed as a Windows Service for production usage.

Repository layout

src/OrderProcessor.Worker/ — Worker service project
FileWatcherService.cs — uses FileSystemWatcher to detect new files
OrderProcessingService.cs — parsing, validation, business rules, persistence
Data/AppDbContext.cs — EF Core SQLite DbContext
Models/ — Order, ValidOrder, InvalidOrder, ProcessedFile
IncomingOrders/ — drop-in folder used by default
src/OrderGenerator.Tool/ — CLI tool to generate test JSON files
tests/OrderProcessor.Tests/ — Automated tests (including IdempotencyTests)
sample JSON files included under src/OrderProcessor.Worker/IncomingOrders/
Prerequisites

.NET 6 SDK or newer
Windows (for service install instructions); runtime works cross-platform when run as console
Optional: sqlite3 or DB browser to inspect the DB file
Quick start — run in console (development)

Clone the repo

git clone https://github.com/<your-org>/HighPerformanceOrderProcessor.git
cd HighPerformanceOrderProcessor
Run the Worker in console mode (shows logs in terminal)

dotnet run --project src/OrderProcessor.Worker
The service will:

Ensure the IncomingOrders folder exists (default: src/OrderProcessor.Worker/IncomingOrders)
Start a FileSystemWatcher and process new .json files as they appear
Persist to SQLite database at data/orders.db (relative to worker project) by default
Run the Order Generator to produce 1000 sample orders (example)

dotnet run --project src/OrderGenerator.Tool -- 1000
See "Order generator" section for CLI options.

Run automated tests

dotnet test
Windows Service installation

Publish the worker as a self-contained or framework-dependent app. Example (framework-dependent):

dotnet publish src/OrderProcessor.Worker -c Release -o ./publish
The publish output will contain OrderProcessor.Worker.exe (or OrderProcessor.Worker on non-Windows).

Install Windows Service using sc.exe (run in elevated PowerShell/CMD). Use the full path to the published exe:

sc create OrderProcessor binPath= "C:\path\to\HighPerformanceOrderProcessor\src\OrderProcessor.Worker\publish\OrderProcessor.Worker.exe" start= auto
sc description OrderProcessor "High-Performance Order Processor - watches IncomingOrders and saves to SQLite"
Start the service:

sc start OrderProcessor
To stop and remove:

sc stop OrderProcessor
sc delete OrderProcessor
Notes:

If you publish framework-dependent, ensure the target machine has the required .NET runtime.
For service installs on Windows you may prefer to use NSSM or Windows Service wrappers that give better control of stdout/stderr.
Configuration

Incoming folder path, DB file path, logging level and other settings are configurable via appsettings.json or environment variables. Defaults:
IncomingOrders folder: ./IncomingOrders inside worker project directory
DB file: ./data/orders.db
Logging: console + rolling file (Serilog)
Environment variables override settings (e.g., WORKER_INCOMING_DIR, WORKER_DB_PATH).
How it works (architecture & design)

FileWatcherService

Uses FileSystemWatcher to receive Created/Changed events for *.json.
Enqueues detected file paths into a processing queue for resilient worker threads.
Debounces rapid duplicate events; uses a short delay/verify step to avoid processing while upstream process is still writing.
File ingestion pipeline

Event-driven (FileSystemWatcher) — polling not used.
When a new file is detected, worker attempts to open it with retries and a small backoff to handle files still being written/locked.
Before processing, the file content's SHA256 hash is computed. The hash is checked against the ProcessedFiles table with a UNIQUE constraint. If hash exists, the file is skipped (idempotency).
Processing outcomes (valid/invalid) are stored transactionally in SQLite.
Order processing & business rules

JSON is parsed into an Order model (OrderId GUID, CustomerName, OrderDate, Items, TotalAmount).
Validation rules:
Invalid if TotalAmount < 0
Invalid if CustomerName is missing or empty
Business rule:
If TotalAmount > 1000 → mark as HighValue = true
Valid orders are inserted into ValidOrders.
Invalid orders (including parse errors/corrupted JSON) are inserted into InvalidOrders with a clear FailureReason.
Regardless of outcome, the file's SHA256 hash and processing metadata are stored in ProcessedFiles to prevent reprocessing.
Persistence and schema (SQLite)

Tables:
ValidOrders
Id (PK)
OrderId (GUID)
CustomerName
OrderDate (UTC)
Items (JSON/text)
TotalAmount (decimal)
IsHighValue (bool)
CreatedAt
InvalidOrders
Id (PK)
RawContent (TEXT)
FailureReason (TEXT)
DetectedAt
ProcessedFiles
Id (PK)
FileName
FileHash (SHA256) — UNIQUE constraint
ProcessedAt
Outcome (Valid/Invalid/Error)
Indexes/Constraints:
UNIQUE(FileHash) on ProcessedFiles ensures idempotency by file content.
Optionally, a UNIQUE constraint on ValidOrders.OrderId protects against duplicate logical orders.
Idempotency implementation (explicit)

Each file's full content is hashed using SHA256.
A ProcessedFiles table stores each processed file's hash and metadata with a UNIQUE constraint on the hash.
On any new detected file:
Compute its SHA256.
If the hash already exists, the file is skipped and logged as "Already processed".
If not, the service processes the file and inserts the hash in the same DB transaction as the order insert (so a crash won't leave partial state that allows reprocessing).
This approach handles:
Duplicate file names with identical content
Re-delivery of the same file
Retries and restarts of the service
Order generator (src/OrderGenerator.Tool) This small CLI creates JSON files into the IncomingOrders folder to help evaluate behavior under different conditions.

Usage examples:

Generate 100 valid orders (default):

dotnet run --project src/OrderGenerator.Tool -- 100
Generate 1000 files quickly (burst):

dotnet run --project src/OrderGenerator.Tool -- 1000 --delay 0
Generate 200 files, 20% invalid, 10% corrupted:

dotnet run --project src/OrderGenerator.Tool -- 200 --invalid-rate 0.2 --corrupt-rate 0.1
CLI flags (examples the tool exposes)

(positional) — number of files to generate
--delay — delay between file creations (default ~10ms)
--invalid-rate <0..1> — fraction of files that are invalid (negative TotalAmount or missing CustomerName)
--corrupt-rate <0..1> — fraction of files that are malformed JSON
--output-dir — where to write files (defaults to worker IncomingOrders)
--hold — create file and keep it locked for to simulate writing/locked file scenarios
The generator is intentionally tiny and has its configuration exposed as CLI arguments so evaluators can easily tune scenarios.

Logging & observability

Uses structured logging (ILogger + Serilog).
Important log events:
New file detected
File open/lock retries
Processing started
Validation errors (with reasons)
Order saved (Valid or Invalid)
Exceptions and retry events
Logs are written to console and rolling files (configurable).
The service catches exceptions per-file and continues processing other files; the worker process does not crash for individual failures.
Testing Automated tests exist under tests/OrderProcessor.Tests. At minimum there is an IdempotencyTests.cs that exercises:

Processing the same file twice does not create duplicate DB records.
Run tests:

dotnet test
Manual & integration test scenarios provided and how to repeat them

High-volume bursts

Start the worker:
dotnet run --project src/OrderProcessor.Worker
Run generator to produce 1000 files quickly:
dotnet run --project src/OrderGenerator.Tool -- 1000 --delay 0
Observe logs: you should see many "New file detected", "Processing started", "Order saved" messages and eventual catch-up. The service uses a bounded worker queue to avoid uncontrolled memory growth.
Files still being written / locked files

Use generator with --hold flag (if available) to create a file and keep it locked for N ms:
dotnet run --project src/OrderGenerator.Tool -- 10 --hold 5000
Or manually lock a file from PowerShell:
$fs = [System.IO.File]::Open("src\OrderProcessor.Worker\IncomingOrders\locked.json", 'Open', 'ReadWrite', 'None')
# keep $fs in the session to hold the lock
Start the worker and confirm it retries and eventually processes the file once lock is released.
Corrupted JSON handling

Generate corrupted JSON files:
dotnet run --project src/OrderGenerator.Tool -- 50 --corrupt-rate 0.5
Worker logs parse errors and inserts records into InvalidOrders with FailureReason = "JSON parse error" (or similar).
Invalid orders (business/validation failures)

Generate invalid orders:
dotnet run --project src/OrderGenerator.Tool -- 50 --invalid-rate 1.0
Worker inserts them into InvalidOrders with a clear reason (e.g., "TotalAmount negative", "CustomerName missing").
Idempotency

Drop the same file twice (same content, different filename) and confirm only one DB record created:
Copy a processed file into IncomingOrders with a new name.
Worker compares file hashes, detects duplicate, logs and skips.
Automated test example:
dotnet test --filter Idempotency
Database inspection

Default DB path: src/OrderProcessor.Worker/data/orders.db (relative)
Inspect with sqlite3:
sqlite3 src/OrderProcessor.Worker/data/orders.db
sqlite> .tables
sqlite> select * from ValidOrders limit 10;
Sample JSON files (Examples you can drop into IncomingOrders)

Valid order (regular)

{
  "OrderId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "CustomerName": "Jane Doe",
  "OrderDate": "2024-11-01T12:00:00Z",
  "Items": [{"Sku":"ABC123","Quantity":2,"UnitPrice":25.5}],
  "TotalAmount": 51.0
}
High-value order (business rule triggered)

{
  "OrderId": "b3d9c1e5-6a01-4d1d-9f8f-1a2b3c4d5e6f",
  "CustomerName": "Acme Corp",
  "OrderDate": "2024-11-01T12:00:00Z",
  "Items": [{"Sku":"BIGITEM","Quantity":1,"UnitPrice":1500.0}],
  "TotalAmount": 1500.0
}
Invalid order (negative total)

{
  "OrderId": "f7e8d9c0-1111-2222-3333-444455556666",
  "CustomerName": "John Smith",
  "OrderDate": "2024-11-01T12:00:00Z",
  "Items": [],
  "TotalAmount": -20.0
}
Corrupted JSON (malformed)

{ "OrderId": "bad-json", "CustomerName": "Broken
Design decisions & tradeoffs

FileSystemWatcher (event-driven) chosen because the assignment forbids polling and it is efficient for local filesystem scenarios. It is susceptible to platform-specific quirks and missed events under extremely high load; to mitigate we:
Use an in-memory debounce/queue and verification step
Compute file hash and rely on DB uniqueness (idempotency) so missed duplicates don't cause double-processing
Idempotency using content hashing is robust to filename changes and duplicate deliveries.
SQLite chosen for simplicity and evaluator convenience. For distributed/scale needs it should be replaced by a central RDBMS and a queue (Azure Service Bus, RabbitMQ, Kafka).
The code intentionally focuses on reliability per-file: per-file try/catch, retries on IO, small bounded worker pool to handle bursts without OOM.
Resiliency & production improvements (one improvement)

Replace FileSystemWatcher-based ingestion with a message queue (e.g., Kafka, RabbitMQ, Azure Service Bus). The legacy system would publish messages (or a lightweight forwarder would) to the queue after writing completes. That enables:
Distributed consumers
Reliable retries and DLQs
Better scaling and backpressure Other improvements (if more time):
Move to transactional outbox for cross-service communication.
Add metrics (Prometheus) and health endpoints.
Add more comprehensive integration tests (end-to-end with containerized DB), and CI pipeline.
Troubleshooting & FAQs

"Service isn't processing files": Check the IncomingOrders path configured, ensure worker has permissions to read/write files and DB path, check logs.
"Files processed multiple times": Confirm ProcessedFiles table exists and DB file is persistent between runs (not in ephemeral temp). Ensure no manual database resets.
"Cannot install service": Ensure publish exe path is correct and run sc commands as Administrator.
Contact

Maintainer: rahulbhattsd (GitHub)
Open an issue with details and logs if you hit problems.
License

MIT
Thank you — drop files into the IncomingOrders folder and watch orders flow into the DB. Happy testing!
 
 



