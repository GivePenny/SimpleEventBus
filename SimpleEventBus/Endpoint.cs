using Microsoft.Extensions.Logging;
using SimpleEventBus.Abstractions.Incoming;
using SimpleEventBus.Incoming;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleEventBus
{
    public sealed class Endpoint : IDisposable
    {
        readonly IMessageSource messageSource;
        readonly ILogger<Endpoint> logger;
        readonly bool useConcurrentFetching;
        readonly IBatchSizeProvider batchSizeProvider;
        readonly IIncomingPipeline pipeline;
        Task listeningTask;
        readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        bool isInitialised;
        bool isShutdown;

        public Endpoint(IMessageSource messageSource, IIncomingPipeline pipeline, ILogger<Endpoint> logger, bool useConcurrentFetching, IBatchSizeProvider batchSizeProvider)
        {
            this.messageSource = messageSource;
            this.pipeline = pipeline;
            this.logger = logger;
            this.useConcurrentFetching = useConcurrentFetching;
            this.batchSizeProvider = batchSizeProvider;
        }

        public void Dispose()
        {
            if (!isShutdown)
            {
                logger.LogCritical(
                    $"Endpoint was disposed before sources and sinks were flushed and connections closed.  Call {nameof(ShutDown)}() before calling {nameof(Dispose)}() and ensure the task is awaited. Alternatively use {nameof(StartWaitThenShutDown)}() and ensure the task is awaited.  Risk of deadlock if Dispose() is relied on to stop the listener.");

                ShutDown().Wait(
                    TimeSpan.FromSeconds(70));
            }

            cancellationTokenSource.Dispose();
        }

        public async Task StartListening(CancellationToken initialisationCancellationToken = default)
        {
            await Initialise(initialisationCancellationToken)
                .ConfigureAwait(false);

            listeningTask = Task.Run(() => Listen(cancellationTokenSource.Token));
        }

        public async Task ShutDown()
        {
            cancellationTokenSource.Cancel();

            var capturedTask = listeningTask;
            if (capturedTask != null)
            {
                await capturedTask
                    .ConfigureAwait(false);
                listeningTask = null;
            }

            await messageSource
                .Close()
                .ConfigureAwait(false);

            isShutdown = true;
        }

        public async Task StartWaitThenShutDown(CancellationToken stopListeningToken)
        {
            await Initialise(stopListeningToken)
                .ConfigureAwait(false);

            await Listen(stopListeningToken)
                .ConfigureAwait(false);

            await ShutDown()
                .ConfigureAwait(false);
        }

        private async Task Initialise(CancellationToken cancellationToken)
        {
            if (isInitialised)
            {
                throw new InvalidOperationException("This endpoint has already been started.");
            }

            isInitialised = true;

            logger.LogInformation("Messaging endpoint is initialising.");

            await pipeline
                .Initialise(cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task Listen(CancellationToken cancellationToken)
        {
            logger.LogInformation("Messaging endpoint is listening for messages.");

            var listeningProcesses = new List<Task>();
            for (var listeningProcessIndex = 0;
                listeningProcessIndex < (useConcurrentFetching ? 2 : 1);
                listeningProcessIndex++)
            {
                if (listeningProcessIndex > 0)
                {
                    await Task
                        .Delay(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }

                listeningProcesses.Add(
                    Task.Run(() => FetchAndProcess(cancellationToken)));
            }

            await Task
                .WhenAll(listeningProcesses)
                .ConfigureAwait(false);

            logger.LogInformation("Messaging endpoint has stopped listening for messages.");
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Vast majority of exceptions will be application exceptions but these are thrown by third-party user code, we cannot identify all the types involved.")]
        private async Task FetchAndProcess(CancellationToken cancellationToken)
        {
            var numberRetrievedInLastBatch = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var messages = await messageSource
                        .WaitForNextMessageBatch(
                            batchSizeProvider.CalculateNewRecommendedBatchSize(numberRetrievedInLastBatch),
                            cancellationToken)
                        .ConfigureAwait(false);

                    numberRetrievedInLastBatch = messages?.Count ?? 0;

                    await pipeline
                        .Process(messages, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // Cannot stop the loop as a subscriber would silently just fail.  Need to log and try again.
                    logger.LogError(exception, "Unhandled exception when checking for messages.");
                }
            }
        }
    }
}