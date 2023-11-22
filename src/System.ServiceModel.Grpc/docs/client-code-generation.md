# ServiceModel.Grpc client code generation

## Reflection.Emit by default

By default, all proxies requested from ClientFactory are generated on demand via Reflection.Emit.
Reflection.Emit proxy class is generated only once for the specific contract, no matter how many ClientFactory instances exist.

example 1:

``` c#
var clientFactory = new ClientFactory();

// under the hood:
//   - generate proxy class for IMyContract
//   - return new proxy-class()
clientFactory.CreateClient<IMyContract>(...);

// return new proxy-class()
clientFactory.CreateClient<IMyContract>(...);
```

example 2:

``` c#
var clientFactory = new ClientFactory();

// generate proxy class for IMyContract under the hood
clientFactory.AddClient<IMyContract>();

// return new proxy-class()
clientFactory.CreateClient<IMyContract>(...);
```

## C# source code generator

To enable source code generation:

- add reference to the package [ServiceModel.Grpc.DesignTime](https://www.nuget.org/packages/ServiceModel.Grpc.DesignTime)
- create a static partial class, name doesn't matter
- the class is a placeholder for generated source code
- configure which proxies should be generated via `ImportGrpcServiceAttribute`

``` c#
[ImportGrpcService(typeof(IMyContract1))]
[ImportGrpcService(typeof(IMyContract2))]
internal static partial class MyGrpcServices
{
    // generated code ...
    public static IClientFactory AddMyContract1Client(this IClientFactory clientFactory, Action<ServiceModelGrpcClientOptions> configure = null) {}

    // generated code ...
    public static IClientFactory AddMyContract2Client(this IClientFactory clientFactory, Action<ServiceModelGrpcClientOptions> configure = null) {}
}
```

- register generated proxies in the ClientFactory

``` c#
// create a client factory
var clientFactory = new ClientFactory();

// register proxies for IMyContract1 and IMyContract2
clientFactory
    .AddMyContract1Client()
    .AddMyContract2Client();

// return new MyGrpcServices.MyContract1Client() under the hood
clientFactory.CreateClient<IMyContract1>(channel);

// return new MyGrpcServices.MyContract2Client() under the hood
clientFactory.CreateClient<IMyContract2>(channel);
```
