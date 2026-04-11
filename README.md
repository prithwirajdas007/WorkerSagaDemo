# WorkerSagaDemo

Distributed job processing with stateful sagas in .NET 10.

An API accepts work requests, persists them to PostgreSQL, and dispatches them to a background Worker via RabbitMQ. The Worker runs a Rebus Saga that processes each step on a deferred timer, updating state in Marten between steps. If the workflow exceeds its timeout, it fails gracefully.

## Architecture

```
 Client                    RabbitMQ                    Worker
   |                          |                          |
   |--- POST /jobs ---------> |                          |
   |                     [API writes Job to Postgres]    |
   |                          |--- StartJobCommand ----> |
   |                          |                    Saga initiates
   |                          |                    DeferLocal(1s)
   |                          | <-- ProcessJobStep(0) -- |
   |                          | -----------------------> |
   |                          |                    Step: Validate
   |                          |                    DeferLocal(3s)
   |                          | <-- ProcessJobStep(1) -- |
   |                          | -----------------------> |
   |                          |                    Step: Process
   |                          |                    DeferLocal(3s)
   |                          | <-- ProcessJobStep(2) -- |
   |                          | -----------------------> |
   |                          |                    Step: Finalize
   |                          |                    MarkAsComplete()
   |                          |                          |
   |--- GET /jobs/{id} -----> |                          |
   |<-- 200 (Completed) ----- |                          |
```

Each step runs as a separate message. The gaps between steps are real network round-trips through RabbitMQ, not `Thread.Sleep`. If the Worker crashes mid-workflow, deferred messages survive in the queue and resume when the Worker restarts.

## Tech Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core Minimal API (.NET 10) |
| Messaging | Rebus 8.9 + RabbitMQ |
| Saga | Rebus Sagas with DeferLocal |
| Document DB | Marten 8.28 (PostgreSQL JSONB) |
| Tests | xUnit + Moq + Alba |

## Quick Start

**Prerequisites:** .NET 10 SDK, Docker

```bash
# Start RabbitMQ + PostgreSQL
docker compose up -d

# Terminal 1: API
cd src/WorkerSagaDemo.Api && dotnet run

# Terminal 2: Worker
cd src/WorkerSagaDemo.Worker && dotnet run

# Terminal 3: Create a job and watch it complete
.\demo.ps1
```

Or manually:

```powershell
# Create a job
$job = Invoke-RestMethod -Method POST -Uri http://localhost:5041/jobs
$job.id

# Wait ~15 seconds, then check
Invoke-RestMethod -Uri "http://localhost:5041/jobs/$($job.id)"
```

## Project Structure

```
src/
  WorkerSagaDemo.Api/             POST /jobs, GET /jobs/{id}
  WorkerSagaDemo.Worker/
    Handlers/                     PingMessageHandler (connectivity test)
    Sagas/                        JobProcessingSaga + SagaData
  WorkerSagaDemo.Contracts/
    Contracts/                    StartJobCommand, ProcessJobStep, JobTimedOut
    Domain/                       Job, JobStep
  WorkerSagaDemo.Tests/
    Sagas/                        7 saga unit tests (mocked, no infra needed)
    Integration/                  2 API tests via Alba (needs PostgreSQL)
```

## Tests

```bash
# All tests
dotnet test

# Saga unit tests only (no Docker needed)
dotnet test --filter "FullyQualifiedName~Sagas"

# Integration tests (needs PostgreSQL running)
dotnet test --filter "FullyQualifiedName~Integration"
```

## Job Lifecycle

```
Queued -> Processing:Validate -> Processing:Process -> Processing:Finalize -> Completed
                                                                           \-> Failed (30s timeout)
```

## Message Flow

| Message | Direction | Trigger |
|---|---|---|
| `StartJobCommand` | API to Worker | POST /jobs creates Job, sends command with just the ID |
| `ProcessJobStep` | Worker to self (DeferLocal) | Saga processes one step, schedules next with 3s delay |
| `JobTimedOut` | Worker to self (DeferLocal) | Scheduled at saga start, fires after 30s if not complete |

## Infrastructure

| Service | Port | UI |
|---|---|---|
| RabbitMQ | 5675 (AMQP) | http://localhost:15675 (guest/guest) |
| PostgreSQL | 5435 | db: worker_demo, user: postgres |

## Roadmap

- [ ] Transactional Outbox (Marten + Rebus)
- [ ] SignalR real-time status notifications
- [ ] .NET Aspire orchestration
- [ ] OpenTelemetry distributed tracing
- [ ] Aspire MCP (AI-assisted debugging)
- [ ] Semantic Kernel (AI-driven saga step)

## License

MIT
