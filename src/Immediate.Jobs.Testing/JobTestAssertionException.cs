namespace Immediate.Jobs.Testing;

/// <summary>Indicates that a job harness assertion did not match captured durable work.</summary>
public sealed class JobTestAssertionException(string message) : Exception(message);
