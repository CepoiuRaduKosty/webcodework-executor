using System.Net.Http.Headers;
using WebCodeWorkExecutor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddDockerClient(builder.Configuration);
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "BackendApiKey";
    options.DefaultChallengeScheme = "BackendApiKey";
    options.DefaultScheme = "BackendApiKey";
})
.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("BackendApiKey", options =>
{
    options.ApiKeyHeaderName = builder.Configuration.GetValue<string>("Backend:ApiHeaderName")!;
    options.ValidApiKey = builder.Configuration.GetValue<string>("Backend:ApiKey")!;
})
.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ContainersApiKey", options =>
{
    options.ApiKeyHeaderName = builder.Configuration.GetValue<string>("Containers:ApiHeaderName")!;
    options.ValidApiKey = builder.Configuration.GetValue<string>("Containers:ApiKey")!;
});

builder.Services.AddScoped<ICodeExecutionService, DockerCodeExecutionService>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(); 
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient("BackendClient", client =>
{
    client.BaseAddress = new Uri($"{builder.Configuration.GetValue<string>("Backend:Address")!}");
    client.DefaultRequestHeaders.Add(builder.Configuration.GetValue<string>("Backend:ApiHeaderName")!, builder.Configuration.GetValue<string>("Backend:ApiKey"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("ContainersClient", client =>
{
    client.BaseAddress = new Uri($"{builder.Configuration.GetValue<string>("Containers:Address")!}");
    client.DefaultRequestHeaders.Add(builder.Configuration.GetValue<string>("Containers:ApiHeaderName")!, builder.Configuration.GetValue<string>("Containers:ApiKey"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddScoped<IBackendService, BackendService>();
builder.Services.AddScoped<IEvaluationContainerService, EvaluationContainerService>();
builder.Services.AddSingleton<ContainerJobsTrackerService>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication(); 
app.UseAuthorization(); 
app.MapControllers();

app.Run();