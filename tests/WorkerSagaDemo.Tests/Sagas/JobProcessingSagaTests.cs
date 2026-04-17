extern alias Worker;

using Marten;
using Microsoft.Extensions.Logging;
using Moq;
using Rebus.Bus;
using Rebus.Sagas;
using WorkerSagaDemo.Contracts.Contracts;
using WorkerSagaDemo.Contracts.Domain;
using Worker::WorkerSagaDemo.Worker.Ai;
using Worker::WorkerSagaDemo.Worker.Sagas;

namespace WorkerSagaDemo.Tests.Sagas;

public class JobProcessingSagaTests
{
    private readonly Mock<IDocumentSession> _sessionMock = new();
    private readonly Mock<ILogger<JobProcessingSaga>> _loggerMock = new();
    private readonly Mock<IBus> _busMock = new();
    private readonly Mock<IJobAiService> _aiServiceMock = new();
    private readonly List<(TimeSpan Delay, object Message)> _deferred = new();
    private readonly List<object> _published = new();

    public JobProcessingSagaTests()
    {
        // Default AI classification result for all tests unless overridden.
        _aiServiceMock
            .Setup(ai => ai.ClassifyTradeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Classification(
                TradeCategory.InterestRateSwap,
                RiskTier.Medium,
                "Default test classification"));
    }

    private JobProcessingSaga CreateSaga(JobProcessingSagaData? data = null)
    {
        _busMock
            .Setup(b => b.DeferLocal(It.IsAny<TimeSpan>(), It.IsAny<object>(), It.IsAny<IDictionary<string, string>>()))
            .Returns<TimeSpan, object, IDictionary<string, string>>((delay, msg, _) =>
            {
                _deferred.Add((delay, msg));
                return Task.CompletedTask;
            });

        _busMock
            .Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<IDictionary<string, string>>()))
            .Returns<object, IDictionary<string, string>>((msg, _) =>
            {
                _published.Add(msg);
                return Task.CompletedTask;
            });

        var saga = new JobProcessingSaga(
            _sessionMock.Object,
            _loggerMock.Object,
            _busMock.Object,
            _aiServiceMock.Object);
        var sagaData = data ?? new JobProcessingSagaData();
        typeof(Saga<JobProcessingSagaData>).GetProperty("Data")!.SetValue(saga, sagaData);
        return saga;
    }

    private Job CreateTestJob(Guid jobId, string? description = "5Y IRS, USD 50M notional")
    {
        return new Job
        {
            Id = jobId,
            Status = "Queued",
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
            Steps = new List<JobStep>
            {
                new() { Name = "Validate" },
                new() { Name = "Process" },
                new() { Name = "Finalize" }
            }
        };
    }

    // ---------------------------------------------------------------
    // Existing saga tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task Handle_StartJobCommand_InitializesSagaState()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId);
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var saga = CreateSaga();
        await saga.Handle(new StartJobCommand(jobId));

        var data = (JobProcessingSagaData)typeof(Saga<JobProcessingSagaData>).GetProperty("Data")!.GetValue(saga)!;
        Assert.Equal(jobId, data.JobId);
        Assert.Equal(3, data.TotalSteps);
        Assert.Equal(0, data.CompletedSteps);
        Assert.Equal(2, _deferred.Count);
        Assert.IsType<JobTimedOut>(_deferred[0].Message);
        Assert.Equal(TimeSpan.FromSeconds(30), _deferred[0].Delay);
        Assert.IsType<ProcessJobStep>(_deferred[1].Message);
        Assert.Equal(TimeSpan.FromSeconds(1), _deferred[1].Delay);
    }

    [Fact]
    public async Task Handle_StartJobCommand_JobNotFound_MarksComplete()
    {
        var jobId = Guid.NewGuid();
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync((Job?)null);

        var saga = CreateSaga();
        await saga.Handle(new StartJobCommand(jobId));

        Assert.Empty(_deferred);
    }

    [Fact]
    public async Task Handle_ProcessJobStep_ProcessesOneStep()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId);
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var sagaData = new JobProcessingSagaData { JobId = jobId, TotalSteps = 3, CompletedSteps = 0 };
        var saga = CreateSaga(sagaData);

        await saga.Handle(new ProcessJobStep(jobId, 0));

        Assert.Equal("Completed", job.Steps[0].Status);
        Assert.NotNull(job.Steps[0].CompletedAt);
        Assert.Equal(1, sagaData.CompletedSteps);
        Assert.Single(_deferred);
        Assert.IsType<ProcessJobStep>(_deferred[0].Message);
        Assert.Equal(TimeSpan.FromSeconds(3), _deferred[0].Delay);
        var nextStep = (ProcessJobStep)_deferred[0].Message;
        Assert.Equal(1, nextStep.StepIndex);
    }

    [Fact]
    public async Task Handle_ProcessJobStep_AllStepsComplete_MarksJobCompleted()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId);
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var sagaData = new JobProcessingSagaData { JobId = jobId, TotalSteps = 3, CompletedSteps = 3 };
        var saga = CreateSaga(sagaData);

        await saga.Handle(new ProcessJobStep(jobId, 3));

        Assert.Equal("Completed", job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.True(sagaData.IsComplete);
        _sessionMock.Verify(s => s.Store(job), Times.Once);
        _sessionMock.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ProcessJobStep_SkipsWhenComplete()
    {
        var jobId = Guid.NewGuid();
        var sagaData = new JobProcessingSagaData { JobId = jobId, IsComplete = true };
        var saga = CreateSaga(sagaData);

        await saga.Handle(new ProcessJobStep(jobId, 0));

        _sessionMock.Verify(s => s.LoadAsync<Job>(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_JobTimedOut_FailsJob()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId);
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var sagaData = new JobProcessingSagaData { JobId = jobId, TotalSteps = 3, CompletedSteps = 1 };
        var saga = CreateSaga(sagaData);

        await saga.Handle(new JobTimedOut(jobId));

        Assert.Equal("Failed", job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.True(sagaData.IsComplete);
    }

    [Fact]
    public async Task Handle_JobTimedOut_SkipsWhenComplete()
    {
        var jobId = Guid.NewGuid();
        var sagaData = new JobProcessingSagaData { JobId = jobId, IsComplete = true };
        var saga = CreateSaga(sagaData);

        await saga.Handle(new JobTimedOut(jobId));

        _sessionMock.Verify(s => s.LoadAsync<Job>(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // Classification tests (Session C + D)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Handle_StartJobCommand_ClassifiesTradeDescription()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId, "5Y CDS on JPMorgan, USD 25M notional");
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        _aiServiceMock
            .Setup(ai => ai.ClassifyTradeAsync("5Y CDS on JPMorgan, USD 25M notional", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Classification(
                TradeCategory.CreditDefaultSwap,
                RiskTier.High,
                "CDS on investment-grade corporate"));

        var saga = CreateSaga();
        await saga.Handle(new StartJobCommand(jobId));

        var data = (JobProcessingSagaData)typeof(Saga<JobProcessingSagaData>).GetProperty("Data")!.GetValue(saga)!;
        Assert.Equal("CreditDefaultSwap", data.TradeCategory);
        Assert.Equal("High", data.RiskTier);
        Assert.Equal("CDS on investment-grade corporate", data.ClassificationRationale);
        // Classification also persisted on Job document
        Assert.Equal("CreditDefaultSwap", job.TradeCategory);
        Assert.Equal("High", job.RiskTier);
        // Still scheduled normal steps
        Assert.Equal(2, _deferred.Count);
    }

    [Fact]
    public async Task Handle_StartJobCommand_ClassifierUnavailable_ContinuesWithUnknown()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId);
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        _aiServiceMock
            .Setup(ai => ai.ClassifyTradeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClassifierUnavailableException(
                "Connection refused",
                new System.Net.Http.HttpRequestException()));

        var saga = CreateSaga();
        await saga.Handle(new StartJobCommand(jobId));

        var data = (JobProcessingSagaData)typeof(Saga<JobProcessingSagaData>).GetProperty("Data")!.GetValue(saga)!;
        Assert.Equal("Unknown", data.TradeCategory);
        Assert.Equal("Unknown", data.RiskTier);
        Assert.Equal("Classifier unavailable", data.ClassificationRationale);
        // Unknown also persisted on Job document
        Assert.Equal("Unknown", job.TradeCategory);
        Assert.Equal("Unknown", job.RiskTier);
        // Steps were still scheduled despite classification failure
        Assert.Equal(2, _deferred.Count);
    }

    [Fact]
    public async Task Handle_StartJobCommand_NoDescription_SkipsClassification()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId, description: null);
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var saga = CreateSaga();
        await saga.Handle(new StartJobCommand(jobId));

        var data = (JobProcessingSagaData)typeof(Saga<JobProcessingSagaData>).GetProperty("Data")!.GetValue(saga)!;
        Assert.Null(data.TradeCategory);
        Assert.Null(data.RiskTier);
        Assert.Null(data.ClassificationRationale);
        // Classifier was never called
        _aiServiceMock.Verify(
            ai => ai.ClassifyTradeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Steps were still scheduled
        Assert.Equal(2, _deferred.Count);
    }

    [Fact]
    public async Task Handle_ProcessJobStep_PublishesEnrichedStatusChanged()
    {
        var jobId = Guid.NewGuid();
        var job = CreateTestJob(jobId, "5Y IRS test");
        job.TradeCategory = "InterestRateSwap";
        job.RiskTier = "Medium";
        _sessionMock.Setup(s => s.LoadAsync<Job>(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var sagaData = new JobProcessingSagaData
        {
            JobId = jobId,
            TotalSteps = 3,
            CompletedSteps = 0,
            TradeCategory = "InterestRateSwap",
            RiskTier = "Medium"
        };
        var saga = CreateSaga(sagaData);

        await saga.Handle(new ProcessJobStep(jobId, 0));

        // Verify the published event carries enrichment fields
        Assert.NotEmpty(_published);
        var evt = Assert.IsType<JobStatusChanged>(_published[0]);
        Assert.Equal("5Y IRS test", evt.Description);
        Assert.Equal("InterestRateSwap", evt.TradeCategory);
        Assert.Equal("Medium", evt.RiskTier);
    }
}
