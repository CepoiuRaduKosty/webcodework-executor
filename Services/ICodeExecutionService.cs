

namespace WebCodeWorkExecutor.Services
{
    
    public record ExecutionRequest(string Code, string Input);
    public record ExecutionResult(bool Success, string? Output, string? Error);

    public interface ICodeExecutionService
    {
        Task<string?> StartCodeEvalAsync(
            string language,
            string codeFilePath,
            int submissionId,
            List<TestCaseInfo> testCases,
            Action<Exception> onException
        );

        Task ForceStopEvaluation(string id);
    }
}