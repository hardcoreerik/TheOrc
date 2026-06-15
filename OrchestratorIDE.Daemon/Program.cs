// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrchestratorIDE.Daemon;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<DaemonConfig>(ctx.Configuration.GetSection("Hive"));
        services.AddHostedService<HiveService>();
    })
    .Build();

await host.RunAsync();
