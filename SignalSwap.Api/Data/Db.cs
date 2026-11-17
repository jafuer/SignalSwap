using Microsoft.Data.SqlClient;

namespace SignalSwap.Api.Data;

public sealed class Db(string connectionString)
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
