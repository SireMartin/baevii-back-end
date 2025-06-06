﻿using baevii_back_end;
using baevii_back_end.Configuration;
using baevii_back_end.DTO;
using baevii_back_end.Models;
using baevii_back_end.Services;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Runtime.ConstrainedExecution;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add CORS services
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAllOrigins",
        policy =>
        {
            policy.AllowAnyOrigin() // Allows any origin
                  .AllowAnyMethod()  // Allows any HTTP method (GET, POST, PUT, DELETE, etc.)
                  .AllowAnyHeader(); // Allows any HTTP headers
        });
});

builder.Services.Configure<PrivyConfiguration>(builder.Configuration.GetSection("Privy"));
builder.Services.AddTransient<PrivyAuthHandler>();

builder.Services.AddTransient<IFunder, Funder>();

builder.Services.AddDbContext<BeaviiDbContext>(options =>
    options.UseSqlServer(builder.Configuration["Mssql:ConnStr"], o =>
    {
        o.EnableRetryOnFailure();
        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    }));

builder.Configuration["Serilog:WriteTo:2:Args:connectionString"] = String.Format(builder.Configuration["Serilog:WriteTo:2:Args:connectionString"], builder.Configuration["AzureBlobStorageAccountKey"]);
builder.Services.AddSerilog(configureLogger =>
{
    configureLogger.ReadFrom.Configuration(builder.Configuration);
});

builder.Services.AddHttpClient("Privy", client =>
{
    client.BaseAddress = new Uri("https://api.privy.io/v1/");
})
.AddHttpMessageHandler<PrivyAuthHandler>(); ;

//swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//builder.Services.AddHttpLogging(logging =>
//{
//    logging.LoggingFields = HttpLoggingFields.All; // Log everything: headers, body, etc.
//    logging.RequestBodyLogLimit = 4096; // Limit for request body logging (bytes)
//    logging.ResponseBodyLogLimit = 4096; // Limit for response body logging (bytes)
//    logging.CombineLogs = true; // Combine request/response logs into one entry
//});

var app = builder.Build();

//app.UseHttpLogging();

//swagger
app.UseSwagger();
app.UseSwaggerUI();

// Use CORS middleware - This must be placed after UseRouting and before UseAuthorization (if present) and endpoint mapping.
// In Minimal APIs, the routing middleware is implicitly added.
app.UseCors("AllowAllOrigins"); // Apply the "AllowAllOrigins" policy globally [1][5]

//app.UseHttpsRedirection();

app.MapGet("/readyness", () => Results.Ok());
app.MapGet("/liveness", () => Results.Ok());

app.MapGet("/user/{userId}", async (BeaviiDbContext dbContext, IHttpClientFactory httpClientFactory, [FromRoute] string? userId) =>
{
    ServerWallet? serverWalletForUser = await dbContext.serverWallets.Include(x => x.User).FirstOrDefaultAsync(x => x.User.PrivyId == userId);
    return Results.Ok(new { serverWalletForUser?.PrivyId, serverWalletForUser?.Address });
});

app.MapPost("/privy", async (PrivyWebhook privyWebhook, BeaviiDbContext dbContext, IHttpClientFactory httpClientFactory, IFunder funder, ILogger<Program> logger) =>
{
logger.LogInformation($"Privy MsgType {privyWebhook.type} received");
    switch (privyWebhook.type)
    {
        case "user.created":
            User newUser = new User
            {
                PrivyId = privyWebhook.user.id,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(privyWebhook.user.created_at)
            };
            dbContext.users.Add(newUser);
            logger.LogInformation($"Created new user with privyId {privyWebhook.user.id}");
            await dbContext.SaveChangesAsync();
            HttpClient privyClient = httpClientFactory.CreateClient("Privy");
            CreateWallet createWallet = new CreateWallet { ChainType = "ethereum" };
            HttpResponseMessage createWalletResponseMsg = await privyClient.PostAsJsonAsync(CreateWallet.RouteTemplate, createWallet);
            string? createWalletResponseContent = await createWalletResponseMsg.Content.ReadAsStringAsync();
            logger.LogDebug("createWalletResponseContent = " + createWalletResponseContent);
            CreateWallet.Response? createWalletResponse = JsonSerializer.Deserialize<CreateWallet.Response>(createWalletResponseContent);
            logger.LogInformation($"Created server wallet with id {createWalletResponse?.id} and address {createWalletResponse?.address}");
            dbContext.serverWallets.Add(new ServerWallet
            {
                PrivyId = createWalletResponse?.id,
                Address = createWalletResponse?.address,
                ChainType = createWalletResponse?.chain_type,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(createWalletResponse?.created_at ?? 0),
                User = newUser
            });
            //fund the wallet to provide the gas for the agent
            await funder.Fund(createWalletResponse?.address);
            await dbContext.SaveChangesAsync();
            break;

        case "user.wallet_created":
            User? user = await dbContext.users.Include(x => x.Accounts).FirstOrDefaultAsync(x => x.PrivyId == privyWebhook.user.id);
            if (user is null)
            {
                throw new Exception($"Wallet Created webhook received for unknowm user with id {privyWebhook.user.id}");
            }
            dbContext.accounts.AddRange(privyWebhook.user.linked_accounts.Select(x => new Account
            {
                Address = x.address,
                ChainId = x.chain_id,
                ChainType = x.chain_type,
                ConnectorType = x.connector_type,
                Type = x.type,
                UserId = user.Id,
                WalletClient = x.wallet_client,
                WalletClientType = x.wallet_client_type
            }));
            await dbContext.SaveChangesAsync();
            break;

        default:
            logger.LogWarning($"Unimplemented msg received (type {privyWebhook.type}");
            break;
    }
});

await app.RunAsync();