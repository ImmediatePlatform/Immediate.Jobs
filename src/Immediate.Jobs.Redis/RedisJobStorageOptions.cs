using Immediate.Validations.Shared;

namespace Immediate.Jobs.Redis;

/// <summary>Configures Redis key placement for Immediate.Jobs.</summary>
[Validate]
public sealed partial class RedisJobStorageOptions : IValidationTarget<RedisJobStorageOptions>
{
	/// <summary>The logical Redis database. The server default is used when this is negative.</summary>
	/// <value>The zero-based logical database number, or a negative value to use the server default.</value>
	public int Database { get; set; } = -1;

	/// <summary>
	/// Prefix for every provider key. Braces are not allowed because the provider adds a Redis
	/// Cluster hash tag that keeps each atomic Lua operation in one slot.
	/// </summary>
	/// <value>The prefix prepended to provider keys.</value>
	[NotEmpty]
	public string KeyPrefix { get; set; } = "immediate-jobs";

	private static void AdditionalValidations(ValidationResult errors, RedisJobStorageOptions target)
	{
		if (target.KeyPrefix.ContainsAny('{', '}'))
		{
			errors.Add(
				new()
				{
					PropertyName = nameof(KeyPrefix),
					ErrorMessage = "The Redis key prefix cannot contain '{' or '}'.",
				}
			);
		}
	}
}
