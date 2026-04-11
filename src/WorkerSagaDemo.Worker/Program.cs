using Marten;
using Microsoft.Extensions.Hosting;
using Rebus.Config;
using Rebus.RabbitMq;
using Rebus.Persistence.InMem;
using Rebus.ServiceProvider;
using WorkerSagaDemo.Worker.Handlers;
using JasperFx;

var builder = Host.CreateApplicationBuilder(args);

// Marten: same connection as API
builder.Services.AddMarten(options =>
{
    options.Connection("Host=localhost;Port=5435;Database=worker_demo;Username=postgres;Password=postgres");
    options.AutoCreateSchemaObjects = AutoCreate.All;
});

// Rebus: worker consumer with saga storage
builder.Services.AddRebus(configure => configure
    .Transport(t => t.UseRabbitMq(
        "amqp://guest:guest@localhost:5675",
        "worker-saga-demo-worker"
    ))
    .Sagas(s => s.StoreInMemory())
    .Timeouts(t => t.StoreInMemory())
    .Options(o =>
    {
        o.SetNumberOfWorkers(1);
        o.SetMaxParallelism(1);
    })
);

builder.Services.AutoRegisterHandlersFromAssemblyOf<PingMessageHandler>();

var host = builder.Build();
host.Run();
