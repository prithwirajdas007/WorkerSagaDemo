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
   |<--202 Accepted----|                     |--StartJobCmd----->|                    |
   |                   |                     |  (+ JobSnapshot)  |                    |
   |                   |                     |                   | Classify(AI/LLM)   |
   |                   |                     |                   |  from snapshot     |
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
- **Enriched outbox events** carrying an immutable snapshot composed inside the creating transaction, with a mid-tier test that proves the thin (ID-only) version published stale data
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
| Tests | xUnit 2.9 + Moq 4.20 + Alba 8.5 + Testcontainers 4.13 + Rebus.TestHelpers |

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
    Contracts/                    StartJobCommand, JobSnapshot, ProcessJobStep, JobTimedOut, JobStatusChanged
    Domain/                       Job, JobStep
  WorkerSagaDemo.AppHost/         Aspire orchestrator (Postgres, RabbitMQ, API, Worker)
  WorkerSagaDemo.ServiceDefaults/ OpenTelemetry + health check wiring
tests/
  WorkerSagaDemo.Tests/
    Ai/                           10 classifier unit tests (mocked IChatCompletionService)
    Sagas/                        13 saga unit tests (mocked, no infra needed)
    Integration/                  5 API tests via Alba (needs PostgreSQL on 5435)
  WorkerSagaDemo.MidTierTests/
    Infrastructure/               Testcontainers fixture, saga harness, message gate, recording classifier
    HarnessSmokeTests.cs          Full outbox-to-saga path over real Postgres + RabbitMQ
    OutboxStalenessTests.cs       Proof that thin events publish stale data (fails pre-fix)
  WorkerSagaDemo.LoadTests/       NBomber outbox + mixed scenarios
```

## Tests

30 tests total across 3 tiers. Classifier and saga tests are pure unit tests with no external dependencies. The mid-tier tests boot the real API and a real Worker against throwaway Testcontainers (Postgres 16 + RabbitMQ 3) and assert on actual wire payloads; they need Docker running and take about 15 seconds warm.

```bash
# All tests (needs Docker for mid-tier, PostgreSQL on 5435 for Alba integration)
dotnet test

# Classifier unit tests only
dotnet test --filter "FullyQualifiedName~Ai"

# Saga unit tests only
dotnet test --filter "FullyQualifiedName~Sagas"

# Mid-tier tests only (Testcontainers, needs Docker)
dotnet test tests/WorkerSagaDemo.MidTierTests
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
| `StartJobCommand` | API to Worker | POST /jobs creates Job + outbox insert (atomic); carries a `JobSnapshot` composed inside that transaction |
| `ProcessJobStep` | Worker to self (DeferLocal) | Saga processes one step, schedules next with 3s delay |
| `JobTimedOut` | Worker to self (DeferLocal) | Scheduled at saga start, fires after 30s if not complete |
| `JobStatusChanged` | Worker publishes, API subscribes | Every status transition, forwarded to SignalR clients |

## Enriched Outbox Events

`StartJobCommand` originally carried only a JobId (48 bytes on the wire) and the saga loaded the Job from Postgres to get the description for classification. That is two reads of a moving target:

1. T0: `POST /jobs` commits the Job document and the outbox row in one transaction. The event means "job created as of T0".
2. T1: `PATCH /jobs/{id}` amends the description while the command is in flight (outbox forwarder poll, RabbitMQ hop, consumer backlog).
3. T2: the Worker handles the command, re-reads the document, and enriches the event with T1 state.

Under low load and single-threaded tests, T1 never happens and everything looks correct. The window was always there; nothing wrote into it until the amendment endpoint existed.

### The proof

`tests/WorkerSagaDemo.MidTierTests/OutboxStalenessTests.cs` holds the command at the Worker with a deterministic gate (a Rebus pipeline step blocking on a `TaskCompletionSource`, no sleeps), amends the description inside the window, releases, and asserts the classifier saw the description committed with the event. Verbatim failure against the thin contract:

```
Assert.Equal() Failure: Strings differ
Expected: "5Y IRS, USD 50M notional, SOFR vs fixed 4"...
Actual:   "AMENDED: 10Y USD payer swaption, USD 250M"...
```

The same test also asserts on the wire payload itself: the published bytes must contain the committed description and must not contain the amendment.

### The fix

`POST /jobs` composes an immutable `JobSnapshot` (Description, StepNames, CapturedAt, SchemaVersion) inside the same Npgsql transaction as the document write, and the outbox row carries it. The transaction boundary is the correctness guarantee: the snapshot is built from exactly the state being committed, and both become visible atomically. The saga consumes the snapshot and never re-reads state the event already carries; its only document access in `Handle(StartJobCommand)` is writing classification results back for the UI.

`JobSnapshot` is a deliberate contract, not the `Job` document serialized onto the wire. It carries the 2 things the consumer needs (description, step names) plus 2 bookkeeping fields (CapturedAt, SchemaVersion), and omits status, timestamps, and classification results on purpose. Serializing the internal model is how a database shape silently becomes a public contract.

### Measured costs and tradeoffs

- **Payload size:** 48 bytes thin vs 257 bytes enriched for a representative job (5.4x). Measured off the wire by the harness; `HarnessSmokeTests` prints the payload and byte count on every run. At this entity size the cost is noise. For entities with large collections it would not be, which is when you snapshot a projection of the entity, not the entity.
- **Schema versioning:** the snapshot carries `SchemaVersion` (currently 1). A consumer receiving a version newer than it understands throws rather than guessing, which routes the message to Rebus retry and then the error queue. Additive changes (new optional fields) do not require a bump; shape changes do, and then producers and consumers must be deployed in a compatible order or the consumer taught both versions.
- **Model leakage:** the only thing stopping `Job` from becoming the public contract is the discipline of composing `JobSnapshot` field by field. There is no compiler guardrail; a code reviewer has to reject `new JobSnapshot(job)` style shortcuts. In this repo the contract lives in `WorkerSagaDemo.Contracts`, which both sides reference. That shared assembly is itself a coupling tradeoff: across real service boundaries you would publish a schema (JSON Schema, Avro, protobuf) rather than share a DLL, precisely so producer and consumer can version independently.
- **When thin events are still right:** very large entities where the snapshot dwarfs the queue's message budget; fan-out to consumers with wildly different needs, where any one snapshot is wrong for most of them; and consumers that genuinely want latest state rather than point-in-time (a cache invalidator wants "go re-read", not "here is what it was"). Thin plus re-read is a valid design when the consumer's question is "what is it now", and a bug only when the question is "what was it when this happened".
- **Ordering:** enrichment-by-re-read also breaks ordering. Two events published in order can carry state observed in the wrong order, because each consumer read happens at an arbitrary later time. Snapshots make ordering guarantees on the wire meaningful again: each message carries the state of its own moment, whatever order handling occurs in. This repo demonstrates the single-event staleness case; the two-event race is the same mechanism twice.

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
