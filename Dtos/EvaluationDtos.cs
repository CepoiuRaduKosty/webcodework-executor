// Services/EvaluationDtos.cs (Create or add to this file)
using System.ComponentModel.DataAnnotations;

namespace WebCodeWorkExecutor.Services // Or your appropriate namespace
{
    public record TestCaseInfo(
        string InputFilePath,
        string ExpectedOutputFilePath,
        int MaxExecutionTimeMs, 
        int MaxRamMB,
        string? TestCaseId = null 
    );

    public record TestCaseEvaluationResult(
        string TestCaseInputPath,
        string Status,
        string? Stdout,
        string? Stderr,
        string? Message,    
        long? DurationMs,
        bool MaximumMemoryException
    );

    public record SolutionEvaluationResult(
        bool CompilationSuccess,
        string? CompilerOutput,
        List<TestCaseEvaluationResult> TestCaseResults
    );

    // --- DTOs for calling the Runner API's single /execute endpoint ---

    internal record RunnerTestCaseItemDto
    {
        public string InputFilePath { get; set; } = string.Empty;
        public string ExpectedOutputFilePath { get; set; } = string.Empty; // Runner needs this to compare
        public int TimeLimitMs { get; set; }
        public int MaxRamMB { get; set; }
        public string? TestCaseId { get; set; } // For correlating results back
    }

    internal record RunnerBatchExecuteRequestDto
    {
        public string Language { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string CodeFilePath { get; set; } = string.Empty; // Path in Azure/Azurite
        public List<RunnerTestCaseItemDto> TestCases { get; set; } = new List<RunnerTestCaseItemDto>();
        // Global container limits (can be more generous than individual test case limits)
    }

    internal record RunnerTestCaseResultDto
    {
        public string? TestCaseId { get; set; } // For correlating
        public string Status { get; set; } = "INTERNAL_ERROR"; // Runner's status for this test case
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public long? DurationMs { get; set; }
        public string? Message { get; set; }
        public bool MaximumMemoryException { get; set; }
    }

    internal record RunnerBatchExecuteResponseDto
    {
        public bool CompilationSuccess { get; set; }
        public string? CompilerOutput { get; set; }
        public List<RunnerTestCaseResultDto> TestCaseResults { get; set; } = new List<RunnerTestCaseResultDto>();
    }

     public static class EvaluationStatus // This should be the orchestrator's final status reporting
    {
        public const string Accepted = "ACCEPTED";
        public const string WrongAnswer = "WRONG_ANSWER";
        public const string CompileError = "COMPILE_ERROR";
        public const string RuntimeError = "RUNTIME_ERROR";
        public const string TimeLimitExceeded = "TIME_LIMIT_EXCEEDED";
        public const string MemoryLimitExceeded = "MEMORY_LIMIT_EXCEEDED";
        public const string FileError = "FILE_ERROR";
        public const string LanguageNotSupported = "LANGUAGE_NOT_SUPPORTED";
        public const string InternalError = "INTERNAL_ERROR";
    }
}