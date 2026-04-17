# WorkerSagaDemo

Distributed job processing with stateful sagas, transactional outbox, real-time UI, and AI-powered trade classification in .NET 10.

A browser client submits work via an API that writes atomically to PostgreSQL (Marten + Rebus outbox in one transaction). A background Worker runs a Rebus Saga that classifies the trade description using a local LLM (Semantic Kernel + Ollama), then processes each step on a deferred timer. Every status change is published as an event and pushed to the browser in real-time via SignalR. The entire stack is orchestrated by .NET Aspire with full OpenTelemetry tracing.

## Architecture

```
 Browser              API                 RabbitMQ             Worker             Postgres
   |                   |                     |                   |                    |
   |---POST /jobs ---->|                     |                   |                    |
   |                   |--BEGIN TX--------------------------------------------------->|
   |                   |  Store(Job) + outbox insert (atomic)                         |
   |                   |--COMMIT----------------------------------------------------->|
   |                   |--outbox forwarder-->|                   |                    |
   |<--202 Accepted----|                     |--StartJobCmd---->|                    |
   |                   |                     |                   | Load Job           |
   |                   |                     |                   | Classify(AI/LLM)   |
   |                   |                     |                   | DeferLocal(30s)    |
   |                   |                     |                   | DeferLocal(1s)     |
   |                   |                     |<--ProcessStep(0)--|--Store/Save------->|
   |                   |<--JobStatusChanged--|                   |                    |
   |<--SignalR push----|                     |                   |                    |
   |  [badges appear]  |                     | ... steps 1, 2 ...                    |
   |                   |                     |<--JobStatusChanged-|                   |
   |<--SignalR push----|                     |                   | MarkAsComplete()   |
```

Each step runs as a separate message. The gaps between steps are real network round-trips through RabbitMQ, not `Thread.Sleep`. If the Worker crashes mid-workflow, deferred messages survive in the queue and resume when the Worker restarts.

## What this demonstrates

This project is a portfolio demo that mirrors production patterns from a real derivatives trading platform. Each pattern is implemented end-to-end with tests, not just sketched:

- **Saga orchestration** with correlation, deferred timeouts, and graceful failure
- **Transactional outbox** ensuring exactly-once delivery between Marten and RabbitMQ
- **AI integration** with defensive parsing, structured output, retry, and graceful degradation
- **Real-time push** from background worker to browser via SignalR pub/sub
- **OpenTelemetry** custom spans with semantic tags, visible in the Aspire dashboard
- **Aspire orchestration** with one-command startup and live observability

## Tech Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core Minimal API (.NET 10) |
| Messaging | Rebus 8.9 + RabbitMQ |
| Saga | Rebus Sagas with DeferLocal timeouts |
| Document DB | Marten 8.28 (PostgreSQL JSONB) |
| Outbox | Rebus.PostgreSql transactional outbox |
| Real-time | SignalR (WebSocket push to browser) |
| AI | Semantic Kernel 1.74 + Ollama (llama3.2:3b) |
| Orchestration | .NET Aspire 13.2 (AppHost + ServiceDefaults) |
| Observability | OpenTelemetry (custom saga spans + Aspire dashboard) |
| Tests | xUnit 2.9 + Moq 4.20 + Alba 8.5 + Rebus.TestHelpers |

## Quick Start

### Option 1: Aspire (recommended)

**Prerequisites:** .NET 10 SDK, Docker Desktop

```bash
dotnet run --project src/WorkerSagaDemo.AppHost
```

Aspire starts Postgres, RabbitMQ, API, and Worker automatically. Dashboard URL is printed in the startup logs.

Open http://localhost:5041 and click **Create New Job**. Watch classification badges appear, then steps progress in real-time.

### Option 2: Standalone (docker-compose)

```bash
# Start infrastructure
docker compose up -d

# Terminal 1: Worker
cd src/WorkerSagaDemo.Worker && dotnet run

# Terminal 2: API
cd src/WorkerSagaDemo.Api && dotnet run
```

Open http://localhost:5041 or run `.\demo.ps1` for a CLI demo.

### AI Classification (optional)

Install [Ollama](https://ollama.com) and pull the model:

```bash
ollama pull llama3.2:3b
```

With Ollama running, each job's trade description is classified into a category (InterestRateSwap, ForeignExchange, CreditDefaultSwap, Equity, Commodity) and risk tier (Low, Medium, High). Without Ollama, the classifier degrades gracefully to Unknown and the saga continues normally.

## Project Structure

```
src/
  WorkerSagaDemo.Api/             Minimal API + SignalR hub + outbox endpoint
  WorkerSagaDemo.Worker/
    Ai/                           IJobAiService, OllamaJobAiService, Classification types
    Handlers/                     PingMessageHandler
    Sagas/                        JobProcessingSaga + SagaData
    SagaTelemetry.cs              Custom OTel ActivitySource + tag constants
  WorkerSagaDemo.Contracts/
    Contracts/                    StartJobCommand, ProcessJobStep, JobTimedOut, JobStatusChanged
    Domain/                       Job, JobStep
  WorkerSagaDemo.AppHost/         Aspire orchestrator (Postgres, RabbitMQ, API, Worker)
  WorkerSagaDemo.ServiceDefaults/ OpenTelemetry + health check wiring
tests/
  WorkerSagaDemo.Tests/
    Ai/                           10 classifier unit tests (mocked IChatCompletionService)
    Sagas/                        11 saga unit tests (mocked, no infra needed)
    Integration/                  2 API tests via Alba (needs PostgreSQL)
```

## Tests

23 tests total. Classifier and saga tests are pure unit tests with no external dependencies.

```bash
# All tests
dotnet test

# Classifier unit tests only
dotnet test --filter "FullyQualifiedName~Ai"

# Saga unit tests only
dotnet test --filter "FullyQualifiedName~Sagas"
```

## Job Lifecycle

```
Queued --> [AI Classification] --> Processing:Validate --> Processing:Process --> Processing:Finalize --> Completed
                                                                                                    \-> Failed (30s timeout)
```

The classification step runs inside `Handle(StartJobCommand)` before any processing steps are scheduled. It's non-blocking: if the classifier is unreachable, the saga sets category to Unknown and continues.

## Message Flow

| Message | Direction | Trigger |
|---|---|---|
| `StartJobCommand` | API to Worker | POST /jobs creates Job + outbox insert (atomic) |
| `ProcessJobStep` | Worker to self (DeferLocal) | Saga processes one step, schedules next with 3s delay |
| `JobTimedOut` | Worker to self (DeferLocal) | Scheduled at saga start, fires after 30s if not complete |
| `JobStatusChanged` | Worker publishes, API subscribes | Every status transition, forwarded to SignalR clients |

## AI Classification

The saga classifies each job's trade description using a local LLM before scheduling processing steps:

- **Model:** llama3.2:3b via Ollama (swappable to OpenAI/Azure/Bedrock by changing DI registration)
- **Abstraction:** Semantic Kernel's `IChatCompletionService`, mocked in tests
- **Output:** Structured JSON parsed defensively (strips markdown fences, tolerates leading prose, retries once)
- **Failure modes:** `ClassifierUnavailableException` (network) and `ClassifierParseException` (bad output) are caught in the saga, which degrades to Unknown rather than failing the job
- **Categories:** InterestRateSwap, ForeignExchange, CreditDefaultSwap, Equity, Commodity, Other, Unknown
- **Risk tiers:** Low, Medium, High, Unknown

## OpenTelemetry Spans

Custom saga spans visible in the Aspire dashboard Traces tab:

| Span | Kind | Tags |
|---|---|---|
| `Saga.StartJobCommand` | Consumer | `saga.job_id`, `saga.total_steps`, `saga.outcome` |
| `Saga.ClassifyTrade` | Internal | `saga.trade_category`, `saga.risk_tier`, `saga.outcome` |
| `Saga.ProcessJobStep` | Consumer | `saga.job_id`, `saga.step_index`, `saga.step_name`, `saga.outcome` |
| `Saga.Work.{StepName}` | Internal | `saga.job_id`, `saga.step_name` (isolates simulated work timing) |
| `Saga.JobTimedOut` | Consumer | `saga.job_id`, `saga.outcome` (Error status for dashboard visibility) |

## Infrastructure

| Service | Aspire mode | Standalone (docker-compose) |
|---|---|---|
| API | Auto-started, port 5041 | `dotnet run` in src/WorkerSagaDemo.Api |
| Worker | Auto-started | `dotnet run` in src/WorkerSagaDemo.Worker |
| PostgreSQL | Aspire-managed container | localhost:5435, db: worker_demo |
| RabbitMQ | Aspire-managed container | AMQP: localhost:5675, UI: localhost:15675 |
| Ollama | External (localhost:11434) | Same (install separately) |
| Aspire Dashboard | Auto-opened | N/A |

## License

MIT
