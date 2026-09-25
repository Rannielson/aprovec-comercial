using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Recorrencia.Db;

public static class DapperSetup
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
            return;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.Value = value;
            if (parameter is NpgsqlParameter npgsql)
                npgsql.NpgsqlDbType = NpgsqlDbType.Date;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new InvalidCastException($"Não é possível converter {value.GetType()} em DateOnly."),
        };
    }
}
