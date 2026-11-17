using Microsoft.Data.Sqlite;

namespace AnimaWindows.Data;

/// <summary>Thin helpers over Microsoft.Data.Sqlite for hand-written SQL with named parameters.</summary>
internal static class SqliteExtensions
{
    public static SqliteCommand Make(this SqliteConnection connection, SqliteTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    public static int Exec(this SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.Make(tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public static List<T> Query<T>(
        this SqliteConnection connection, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.Make(tx, sql, parameters);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static int? NInt(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static long? NLong(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}

/// <summary>Element-wise equality for lists, used to suppress duplicate emissions from observation streams.</summary>
internal sealed class ListComparer<T> : IEqualityComparer<List<T>>
{
    public static ListComparer<T> Instance { get; } = new();

    public bool Equals(List<T>? x, List<T>? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Count != y.Count)
        {
            return false;
        }

        var cmp = EqualityComparer<T>.Default;
        for (var i = 0; i < x.Count; i++)
        {
            if (!cmp.Equals(x[i], y[i]))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(List<T> obj) => obj.Count;
}
