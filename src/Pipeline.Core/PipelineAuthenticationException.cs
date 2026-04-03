namespace Pipeline.Core;

public class PipelineAuthenticationException : Exception
{
    public PipelineAuthenticationException(string message) : base(message) { }
    public PipelineAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}
