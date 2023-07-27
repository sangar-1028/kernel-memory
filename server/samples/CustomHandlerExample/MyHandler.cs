﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticMemory.Core.Pipeline;

public class MyHandler : IHostedService, IPipelineStepHandler
{
    private readonly IPipelineOrchestrator _orchestrator;
    private readonly ILogger<MyHandler> _log;

    public MyHandler(
        string stepName,
        IPipelineOrchestrator orchestrator,
        ILogger<MyHandler>? log = null)
    {
        this.StepName = stepName;
        this._orchestrator = orchestrator;
        this._log = log ?? NullLogger<MyHandler>.Instance;
    }

    /// <inheritdoc />
    public string StepName { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        this._log.LogInformation("Starting {0}...", this.GetType().FullName);
        return this._orchestrator.AddHandlerAsync(this, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        this._log.LogInformation("Stopping {0}...", this.GetType().FullName);
        return this._orchestrator.StopAllPipelinesAsync();
    }

    /// <inheritdoc />
    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(DataPipeline pipeline, CancellationToken cancellationToken)
    {
        /* ... your custom ...
         * ... handler ...
         * ... business logic ... */

        // Remove this - here only to avoid build errors
        await Task.Delay(0, cancellationToken).ConfigureAwait(false);

        return (true, pipeline);
    }
}
