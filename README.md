## High-Performance Order Processor

### Run
dotnet run --project src/OrderProcessor.Worker

### Windows Service
sc create OrderProcessor binPath= "OrderProcessor.Worker.exe"

### Generator
dotnet run --project src/OrderGenerator.Tool -- 1000

### Idempotency
Implemented using SHA256 file hashing + unique DB constraint.

### Tests
dotnet test

### Improvement
Replace FileSystemWatcher with message queue for distributed scale.
