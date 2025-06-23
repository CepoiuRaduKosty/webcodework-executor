
using System.ComponentModel.DataAnnotations;

namespace WebCodeWorkExecutor.Services 
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

    

    internal record RunnerTestCaseItemDto
    {
        public string InputFilePath { get; set; } = string.Empty;
        public string ExpectedOutputFilePath { get; set; } = string.Empty; 
        public int TimeLimitMs { get; set; }
        public int MaxRamMB { get; set; }
        public string? TestCaseId { get; set; } 
    }

    internal record RunnerBatchExecuteRequestDto
    {
        public string Language { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string CodeFilePath { get; set; } = string.Empty; 
        public List<RunnerTestCaseItemDto> TestCases { get; set; } = new List<RunnerTestCaseItemDto>();
        
    }

    internal record RunnerTestCaseResultDto
    {
        public string? TestCaseId { get; set; } 
        public string Status { get; set; } = "INTERNAL_ERROR"; 
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

     public static class EvaluationStatus
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