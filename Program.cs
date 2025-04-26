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
builder.Services.AddControllers(); 
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();
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

app.MapControllers();

app.Run();