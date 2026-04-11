namespace WorkerSagaDemo.Contracts.Contracts;
public record ProcessJobStep(Guid JobId, int StepIndex);
