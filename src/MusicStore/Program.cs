using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using EventPipe;

namespace MusicStore
{
    public static class Program
    {
        private static bool ContinueRunning = true;
        private static object ConsoleLock = new object();
        private static int NumThreads = 1;

        public static void Main(string[] args)
        {
            if(args.Length > 0)
            {
                NumThreads = Convert.ToInt32(args[0]);
            }

            Console.WriteLine("Running with {0} worker threads.", NumThreads);
            EnableTracing();

            var totalTime = Stopwatch.StartNew();

            var config = new ConfigurationBuilder()
                .AddCommandLine(args)
                .AddEnvironmentVariables(prefix: "ASPNETCORE_")
                .Build();

            var builder = new WebHostBuilder()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseConfiguration(config)
                .UseIISIntegration()
                .UseStartup("MusicStore")
                .ConfigureLogging(factory =>
                {
                    factory.AddConsole();
                    factory.AddFilter("Console", level => level >= LogLevel.Warning);
                })
                .UseKestrel();

            var host = builder.Build();

            host.Start();

            totalTime.Stop();
            var serverStartupTime = totalTime.ElapsedMilliseconds;
            Console.WriteLine("Server started in {0}ms", serverStartupTime);
            Console.WriteLine();

            using (var client = new HttpClient())
            {
                Console.WriteLine("Starting request to http://localhost:5000");
                var requestTime = Stopwatch.StartNew();
                var response = client.GetAsync("http://localhost:5000").Result;
                response.EnsureSuccessStatusCode(); // Crash immediately if something is broken
                requestTime.Stop();
                var firstRequestTime = requestTime.ElapsedMilliseconds;

                Console.WriteLine("Response: {0}", response.StatusCode);
                Console.WriteLine("Request took {0}ms", firstRequestTime);
                Console.WriteLine();
                Console.WriteLine("Cold start time (server start + first request time): {0}ms", serverStartupTime + firstRequestTime);
                Console.WriteLine();
                Console.WriteLine();
            }

            DisableTracing();

            // Spawn worker threads.
            int numWorkers = 1;
            Console.WriteLine("Spawning {0} workers.", numWorkers);
            Task[] workerTasks = new Task[numWorkers];
            for(int i=0; i<numWorkers; i++)
            {
                workerTasks[i] = new Task(new Action(RunRequests));
                workerTasks[i].Start();
            }

            // Spawn tracing thread.
            Task tracingControllerTask = new Task(new Action(TracingController));
            tracingControllerTask.Start();

            char c;
            do
            {
                Console.WriteLine("Type 'q' to quit.");
                c = Console.ReadKey().KeyChar;
            }
            while(c != 'q');

            // Disable workers.
            Console.WriteLine("Waiting for workers to stop.");
            ContinueRunning = false;
            Task.WaitAll(workerTasks);

            Console.WriteLine("Workers stopped successfully.");

            // Wait for tracing controller to finish.
            Console.WriteLine("Waiting for tracing controller to finish.");
            tracingControllerTask.Wait();
        }

        private static void EnableTracing()
        {
            Console.WriteLine("Start: Enable tracing.");
            TraceControl.EnableDefault();
            Console.WriteLine("Stop: Enable tracing.");
        }

        private static void DisableTracing()
        {
            Console.WriteLine("Start: Disable tracing.");
            TraceControl.Disable();
            Console.WriteLine("Stop: Disable tracing.");
        }

        private static void TracingController()
        {
            while(ContinueRunning)
            {
                // Enable tracing.
                EnableTracing();

                // Wait for 5 seconds.
                System.Threading.Thread.Sleep(5000);

                // Disable Tracing.
                DisableTracing();
            }
        }

        private static void RunRequests()
        {
            var minRequestTime = long.MaxValue;
            var maxRequestTime = long.MinValue;
            var averageRequestTime = 0.0;
            var currentRequestIndex = 1;
            Stopwatch requestTime = new Stopwatch();
            using (var client = new HttpClient())
            {
                while(ContinueRunning)
                {
                    requestTime.Restart();
                    var response = client.GetAsync("http://localhost:5000").Result;
                    requestTime.Stop();

                    var requestTimeElapsed = requestTime.ElapsedMilliseconds;
                    if (requestTimeElapsed < minRequestTime)
                    {
                        minRequestTime = requestTimeElapsed;
                    }

                    if (requestTimeElapsed > maxRequestTime)
                    {
                        maxRequestTime = requestTimeElapsed;
                    }

                    // Rolling average of request times
                    averageRequestTime = (averageRequestTime * ((currentRequestIndex - 1.0) / currentRequestIndex)) + (requestTimeElapsed * (1.0 / currentRequestIndex));
                    currentRequestIndex++;

                    if(currentRequestIndex % 1000 == 0)
                    {
                        lock(ConsoleLock)
                        {
                            Console.WriteLine("Steadystate min response time: {0}ms", minRequestTime);
                            Console.WriteLine("Steadystate max response time: {0}ms", maxRequestTime);
                            Console.WriteLine("Steadystate average response time: {0}ms", (int)averageRequestTime);
                        }
                    }
                }
            }
        }
    }
}
