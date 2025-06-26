using Docker.DotNet;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using WebCodeWorkExecutor.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddDockerClient(builder.Configuration);
builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme,
        options => { /* No custom options needed for this basic handler */ });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiKeyPolicy", policy =>
    {
        policy.AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
    });
});

builder.Services.AddScoped<WebCodeWorkExecutor.Services.ICodeExecutionService, WebCodeWorkExecutor.Services.DockerCodeExecutionService>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(); 
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { });

    options.AddSecurityDefinition(ApiKeyAuthenticationDefaults.AuthenticationScheme, new OpenApiSecurityScheme
    {
        Description = "API Key authentication header.",
        Name = ApiKeyAuthenticationDefaults.ApiKeyHeaderName,
        In = ParameterLocation.Header, 
        Type = SecuritySchemeType.ApiKey, 
        Scheme = "ApiKey" 
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = ApiKeyAuthenticationDefaults.AuthenticationScheme 
                },
                Scheme = "ApiKey",
                Name = ApiKeyAuthenticationDefaults.ApiKeyHeaderName,
                In = ParameterLocation.Header,
            },
            new List<string>() 
        }
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Code Runner API v1");
    });
}

app.UseExceptionHandler();
app.UseAuthentication(); 
app.UseAuthorization(); 
app.MapControllers();

app.Run();