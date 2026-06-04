using System;
using Microsoft.AspNetCore.Builder;
using MusicPlayerSyncServer;

Console.WriteLine("MusicPlayerSyncServer Startup!");
DotNetEnv.Env.Load("../.env", new(setEnvVars: true));

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureWebhost();
builder.RegisterServices();

var app = builder.Build();
app.RegisterMiddlewares();
app.ConfigureWebApp();
app.RegisterNotesEndpointsV1(app.Services);
app.Run();
