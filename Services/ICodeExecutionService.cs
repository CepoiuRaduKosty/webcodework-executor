// Services/ICodeExecutionService.cs
namespace WebCodeWorkExecutor.Services
{
    // Placeholder DTOs for now
    public record ExecutionRequest(string Code, string Input);
    public record ExecutionResult(bool Success, string? Output, string? Error);

    public interface ICodeExecutionService
    {
        // Task to demonstrate starting/stopping, will evolve
        Task<bool> TestContainerLifecycleAsync(string imageName = "alpine:latest");

        // Task for actual execution (implement later)
        // Task<ExecutionResult> ExecuteCodeAsync(ExecutionRequest request);
    }
}