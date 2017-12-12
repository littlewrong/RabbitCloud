﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rabbit.Cloud.Grpc;

namespace Rabbit.Cloud.Server.Grpc.Starter
{
    public class GrpcServerOptions
    {
        public string Host { get; set; }
        public int Port { get; set; }
    }

    public class GrpcServerBootstrap
    {
        public static void Start(IHostBuilder hostBuilder)
        {
            hostBuilder
                .ConfigureServices((ctx, services) =>
                {
                    var grpConfiguration = ctx.Configuration.GetSection("RabbitCloud:Server:Grpc");
                    if (!grpConfiguration.Exists())
                        return;
                    services
                        .Configure<GrpcServerOptions>(grpConfiguration)
                        .AddGrpcServer()
                        .AddServerGrpc()
                        .AddSingleton<IHostedService, GrpcServerHostedService>();
                });
        }
    }
}