namespace Immediate.Jobs.StorageTests;

public enum ConformanceDatabase
{
	Sqlite,
	PostgreSql,
	SqlServer,
}

public enum ConformanceTopology
{
	Distributed,
	SingleServer,
}
