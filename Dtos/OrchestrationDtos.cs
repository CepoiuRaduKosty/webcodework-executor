
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using WebCodeWorkExecutor.Services; 

namespace WebCodeWorkExecutor.Dtos 
{
    
    
    
    
    public class TestCasePathInfo
    {
        [Required]
        public string InputFilePath { get; set; } = string.Empty;

        [Required]
        public string ExpectedOutputFilePath { get; set; } = string.Empty;

        
        public string? TestCaseId { get; set; } 

        [Range(100, 100000)] 
        public int MaxExecutionTimeMs { get; set; } = 2000; 

        [Range(32, 512)]  
        public int MaxRamMB { get; set; } = 128;    
    }

    
    
    
    public class OrchestrationEvaluateRequest
    {
        [Required]
        public string Language { get; set; } = string.Empty; 
        public string? Version { get; set; } 

        [Required]
        public string CodeFilePath { get; set; } = string.Empty; 

        [Required]
        public int SubmissionId { get; set; }

        [Required]
        [MinLength(1, ErrorMessage = "At least one test case must be provided.")]
        public List<TestCasePathInfo> TestCases { get; set; } = new List<TestCasePathInfo>();
    }

    
    
    
    
    public class OrchestrationTestCaseResult
    {
        public string TestCaseInputPath { get; set; } = string.Empty; 
        public string? TestCaseId { get; set; } 

        [Required]
        public string Status { get; set; } = EvaluationStatus.InternalError; 
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public string? Message { get; set; }
        public long? DurationMs { get; set; }
        public string? TestcaseName { get; set; }
    }

    public class OrchestrationEvaluateResponse
    {
        public string OverallStatus { get; set; } = "Error";
        public bool CompilationSuccess { get; set; }
        public string? CompilerOutput { get; set; }
        required public string Language { get; set; }
        public List<OrchestrationTestCaseResult> Results { get; set; } = new List<OrchestrationTestCaseResult>();
    }
}
