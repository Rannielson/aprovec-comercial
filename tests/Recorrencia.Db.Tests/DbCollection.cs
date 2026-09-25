namespace Recorrencia.Db.Tests;

[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "db";
}
