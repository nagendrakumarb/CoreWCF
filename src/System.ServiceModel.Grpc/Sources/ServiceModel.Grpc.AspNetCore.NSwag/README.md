# ServiceModel.Grpc.AspNetCore.NSwag

`ServiceModel.Grpc` enables applications to communicate with gRPC services using a code-first approach (no [.proto files](https://learn.microsoft.com/en-us/aspnet/core/grpc/basics#proto-file)), helps to get around limitations of gRPC protocol like "only reference types", "exact one input", "no nulls", "no value-types". Provides exception handling. Helps to migrate existing WCF solution to gRPC with minimum effort.

`ServiceModel.Grpc.AspNetCore.NSwag` is package with [NSwag](https://github.com/RicoSuter/NSwag) integration for [ServiceModel.Grpc.AspNetCore](https://www.nuget.org/packages/ServiceModel.Grpc.AspNetCore).

## Declare a service contract and implement service

A service contract is an interface decorated with the `ServiceContract` attribute. Interface methods decorated with the `OperationContract` attribute are treated as gRPC operations.

For example, following `ICalculator` contract with unary `Sum` operation and client streaming `MultiplyBy` operation:

```csharp
[ServiceContract]
public interface ICalculator
{
    [OperationContract]
    Task<long> Sum(long x, int y, int z, CancellationToken token = default);

    [OperationContract]
    ValueTask<(int Multiplier, IAsyncEnumerable<int> Values)> MultiplyBy(IAsyncEnumerable<int> values, int multiplier, CancellationToken token = default);
}

internal sealed class Calculator : ICalculator
{
    public Task<long> Sum(long x, int y, int z, CancellationToken token)
    {
        ...
    }

    public ValueTask<(int Multiplier, IAsyncEnumerable<int> Values)> MultiplyBy(IAsyncEnumerable<int> values, int multiplier, CancellationToken token)
    {
        ...
    }
}
```

## Configure NSwag

Enable ServiceModel.Grpc code-first and NSwag integration, add service to the routing pipeline.

```csharp
var builder = WebApplication.CreateBuilder();

// NSwag.AspNetCore
builder.Services.AddMvc();
builder.Services.AddOpenApiDocument(settings =>
{
    settings.DocumentName = "v1";
    settings.Title = "My API";
    settings.Version = "1.0";
});

// enable ServiceModel.Grpc code-first
builder.Services.AddServiceModelGrpc(options => { ... });

// enable ServiceModel.Grpc integration for NSwag.AspNetCore
builder.Services.AddServiceModelGrpcSwagger();

var app = builder.Build();

// NSwag.AspNetCore
app.UseOpenApi();
app.UseSwaggerUi3();
app.UseReDoc();

// Enable ServiceModel.Grpc HTTP/1.1 JSON gateway for Swagger UI, button "Try it out"
app.UseServiceModelGrpcSwaggerGateway();

// bind the service
app.MapGrpcService<Calculator>();

app.Run();
```

![UI demo](https://raw.githubusercontent.com/max-ieremenko/ServiceModel.Grpc/master/Sources/ServiceModel.Grpc.AspNetCore.NSwag/readme-swagger-ui.png)

## Links

- [Documentation](https://max-ieremenko.github.io/ServiceModel.Grpc)
- [ServiceModel.Grpc GitHub](https://github.com/max-ieremenko/ServiceModel.Grpc)
- [Example](https://github.com/max-ieremenko/ServiceModel.Grpc/tree/master/Examples/Swagger)
