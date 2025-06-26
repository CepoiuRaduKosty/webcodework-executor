

namespace WebCodeWorkExecutor.Services
{
    
    public record ExecutionRequest(string Code, string Input);
    public record ExecutionResult(bool Success, string? Output, string? Error);

    public interface ICodeExecutionService
    {
        Task<SolutionEvaluationResult> EvaluateSolutionAsync(
            string language,
            string codeFilePath,
            List<TestCaseInfo> testCases 
        );
    }
}