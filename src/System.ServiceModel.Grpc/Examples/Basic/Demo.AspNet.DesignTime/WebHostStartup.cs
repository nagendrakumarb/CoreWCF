﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Demo.AspNet.DesignTime;

internal sealed class WebHostStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // enable ServiceModel.Grpc
        services.AddServiceModelGrpc();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            // host PersonService endpoint generated by ServiceModel.Grpc.DesignTime
            endpoints.MapPersonService();
        });
    }
}