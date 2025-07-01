namespace Immediate.Jobs.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public sealed class JobAttribute : Attribute
{
	public JobAttribute(string keyId)
	{
		KeyId = keyId;
	}

	public string KeyId { get; }
}
