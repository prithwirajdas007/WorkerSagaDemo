using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Rebus.Bus;
using WorkerSagaDemo.Contracts.Domain;

namespace WorkerSagaDemo.Tests.Integration;

public class JobApiTests : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await AlbaHost.For<Program>(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove Rebus hosted services so we don't connect to RabbitMQ
                var rebusDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType?.FullName?.Contains("Rebus") == true)
                    .ToList();
                foreach (var descriptor in rebusDescriptors)
                {
                    services.Remove(descriptor);
                }

                // Replace IBus with a mock
                services.AddSingleton(new Mock<IBus>().Object);
            });
        });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task POST_jobs_returns_202_and_creates_job()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url("/jobs");
            s.StatusCodeShouldBe(202);
        });

        var response = result.ReadAsJson<JobResponse>();
        Assert.NotNull(response);
        Assert.NotEqual(Guid.Empty, response!.Id);
        Assert.Equal("Queued", response.Status);

        // Verify job exists via GET
        var getResult = await _host.Scenario(s =>
        {
            s.Get.Url($"/jobs/{response.Id}");
            s.StatusCodeShouldBe(200);
        });

        var job = getResult.ReadAsJson<Job>();
        Assert.NotNull(job);
        Assert.Equal("Queued", job!.Status);
        Assert.Equal(3, job.Steps.Count);
    }

    [Fact]
    public async Task GET_jobs_nonexistent_returns_404()
    {
        await _host.Scenario(s =>
        {
            s.Get.Url($"/jobs/{Guid.NewGuid()}");
            s.StatusCodeShouldBe(404);
        });
    }

    private record JobResponse(Guid Id, string Status);
}
