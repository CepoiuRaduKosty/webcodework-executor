using Docker.DotNet;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using WebCodeWorkExecutor.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddProblemDetails(); // Good for standard error responses
builder.Services.AddDockerClient(builder.Configuration);
builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme) // Set default scheme
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme, // Scheme name
        options => { /* No custom options needed for this basic handler */ });
builder.Services.AddAuthorization(options =>
{
    // Optional: Define a policy that requires the ApiKey scheme
    // This makes applying it slightly cleaner if you have multiple schemes
    options.AddPolicy("ApiKeyPolicy", policy =>
    {
        policy.AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        // policy.RequireClaim(...); // Add claim requirements if needed
    });

    // You could make ApiKey the default policy
    // options.DefaultPolicy = options.GetPolicy("ApiKeyPolicy") ?? options.DefaultPolicy;
});

builder.Services.AddScoped<WebCodeWorkExecutor.Services.ICodeExecutionService, WebCodeWorkExecutor.Services.DockerCodeExecutionService>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { /* ... Title, Version etc. ... */ });

    // Define the ApiKey security scheme in Swagger
    options.AddSecurityDefinition(ApiKeyAuthenticationDefaults.AuthenticationScheme, new OpenApiSecurityScheme
    {
        Description = "API Key authentication header.",
        Name = ApiKeyAuthenticationDefaults.ApiKeyHeaderName, // The header name (X-Api-Key)
        In = ParameterLocation.Header, // Where the key is sent
        Type = SecuritySchemeType.ApiKey, // Type of security
        Scheme = "ApiKey" // Can be arbitrary, matches scheme name for reference
    });

    // Make Swagger UI use the ApiKey scheme
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = ApiKeyAuthenticationDefaults.AuthenticationScheme // Must match the ID in AddSecurityDefinition
                },
                Scheme = "ApiKey",
                Name = ApiKeyAuthenticationDefaults.ApiKeyHeaderName,
                In = ParameterLocation.Header,
            },
            new List<string>() // No scopes needed for API Key
        }
    });
});



var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger(); // Serves swagger.json (defaults to /swagger/v1/swagger.json)
    app.UseSwaggerUI(options =>
    {
        // Tells the UI where to find the swagger.json file
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Code Runner API v1");
        // Optional: Serve UI at the app's root (e.g., / instead of /swagger)
        // options.RoutePrefix = string.Empty;
    });
}

// Configure the HTTP request pipeline.
// No Swagger/OpenAPI needed for this service? Add if desired.
// if (app.Environment.IsDevelopment()) { ... }

app.UseExceptionHandler(); // Add basic exception handling
app.UseAuthentication(); // Attempts to authenticate based on registered schemes
app.UseAuthorization();  // Authorizes based on authenticated user/policies

// Example placeholder - replace with actual evaluation endpoint later
app.MapGet("/", () => $"Code Runner Service ({DateTime.UtcNow:O})")
   .WithName("GetServiceStatus")
   .WithTags("Diagnostics")
   .ExcludeFromDescription(); // Optional: Hide simple root from Swagger UI

app.MapPost("/test-docker", async (IDockerClient dockerClient, ILogger<Program> logger) =>
{
    // Simple endpoint to test basic Docker connection
    try
    {
        var images = await dockerClient.Images.ListImagesAsync(new Docker.DotNet.Models.ImagesListParameters { All = true });
        logger.LogInformation("Successfully listed {Count} Docker images.", images.Count);
        return Results.Ok(new { message = $"Connected to Docker. Found {images.Count} images." });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to list Docker images.");
        return Results.Problem($"Failed to connect to Docker: {ex.Message}", statusCode: 500);
    }
})
    .WithName("TestDockerConnection").WithTags("Diagnostics")
    .RequireAuthorization("ApiKeyPolicy"); // <-- PROTECT THIS ENDPOINT (Example)

app.MapPost("/test-lifecycle", async (WebCodeWorkExecutor.Services.ICodeExecutionService executionService, ILogger<Program> logger) =>
{
    logger.LogInformation("Received request for /test-lifecycle");
    bool success = await executionService.TestContainerLifecycleAsync(); // Use default 'alpine'
    if (success)
    {
        return Results.Ok(new { message = "Container lifecycle test completed successfully (exit code 0)." });
    }
    else
    {
        return Results.Problem("Container lifecycle test failed or container returned non-zero exit code.", statusCode: 500);
    }
})
    .WithName("TestContainerLifecycle").WithTags("Diagnostics")
    .RequireAuthorization("ApiKeyPolicy"); // <-- PROTECT THIS ENDPOINT (Example)


app.Run();