using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting.WindowsServices;
using OrthoVisionDriver;
using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService();
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<IAnalyzerLoggerFactory, AnalyzerLoggerFactory>();
builder.Services.AddSingleton<AnalyzerManager>();

var host = builder.Build();
host.Run();
