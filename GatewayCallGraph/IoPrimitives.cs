namespace GatewayCallGraph;

/// <summary>
/// Framework-level I/O primitives. Each entry is a (containingTypeFqn, methodName)
/// pair plus the boundary category that calling it represents. These are the
/// "mechanism" leaves — the actual socket open, file open, SQL open. Everything
/// in source that transitively reaches one of these is doing I/O.
///
/// We match by (typeFqn, methodName) just like BoundarySeeds.Match does, so the
/// rules are uniform. Methods on a *base* type (e.g. DbConnection.Open) match
/// all subclasses via the inferrer's symbol-walk that climbs base types before
/// looking up.
/// </summary>
public static class IoPrimitives
{
    public const string ExternalSystemGateway = "external_system_gateway";
    public const string DatabaseQuery = "database_query";

    public sealed record Primitive(string TypeFqn, string MethodName, string Category);

    public static readonly IReadOnlyList<Primitive> All = new Primitive[]
    {
        // === HTTP egress ===
        new("System.Net.Http.HttpClient", "SendAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "Send", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "GetAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "PostAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "PutAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "DeleteAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "PatchAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "GetStringAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "GetByteArrayAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpClient", "GetStreamAsync", ExternalSystemGateway),
        new("System.Net.Http.HttpMessageInvoker", "SendAsync", ExternalSystemGateway),

        // Legacy WebRequest API — still appears in older codebases.
        new("System.Net.WebRequest", "GetResponse", ExternalSystemGateway),
        new("System.Net.WebRequest", "GetResponseAsync", ExternalSystemGateway),
        new("System.Net.WebRequest", "GetRequestStream", ExternalSystemGateway),
        new("System.Net.WebRequest", "GetRequestStreamAsync", ExternalSystemGateway),
        new("System.Net.HttpWebRequest", "GetResponse", ExternalSystemGateway),
        new("System.Net.HttpWebRequest", "GetResponseAsync", ExternalSystemGateway),
        new("System.Net.HttpWebRequest", "GetRequestStream", ExternalSystemGateway),
        new("System.Net.WebClient", "DownloadString", ExternalSystemGateway),
        new("System.Net.WebClient", "DownloadStringAsync", ExternalSystemGateway),
        new("System.Net.WebClient", "DownloadData", ExternalSystemGateway),
        new("System.Net.WebClient", "UploadString", ExternalSystemGateway),
        new("System.Net.WebClient", "UploadData", ExternalSystemGateway),

        // SOAP — generated proxies inherit SoapHttpClientProtocol; calls go through Invoke.
        new("System.Web.Services.Protocols.SoapHttpClientProtocol", "Invoke", ExternalSystemGateway),
        new("System.Web.Services.Protocols.SoapHttpClientProtocol", "InvokeAsync", ExternalSystemGateway),
        new("System.ServiceModel.ClientBase`1", "Invoke", ExternalSystemGateway),

        // SMTP / mail.
        new("System.Net.Mail.SmtpClient", "Send", ExternalSystemGateway),
        new("System.Net.Mail.SmtpClient", "SendAsync", ExternalSystemGateway),
        new("System.Net.Mail.SmtpClient", "SendMailAsync", ExternalSystemGateway),

        // === Database ===
        // Connection open is the universal "we are now talking to a DB" moment.
        new("System.Data.Common.DbConnection", "Open", DatabaseQuery),
        new("System.Data.Common.DbConnection", "OpenAsync", DatabaseQuery),
        new("System.Data.SqlClient.SqlConnection", "Open", DatabaseQuery),
        new("System.Data.SqlClient.SqlConnection", "OpenAsync", DatabaseQuery),
        new("Microsoft.Data.SqlClient.SqlConnection", "Open", DatabaseQuery),
        new("Microsoft.Data.SqlClient.SqlConnection", "OpenAsync", DatabaseQuery),
        new("System.Data.IDbConnection", "Open", DatabaseQuery),

        // Command execution — covers cases where a connection is opened elsewhere
        // (e.g. via using-block on an injected IDbConnection) but the actual SQL
        // is fired here.
        new("System.Data.Common.DbCommand", "ExecuteReader", DatabaseQuery),
        new("System.Data.Common.DbCommand", "ExecuteReaderAsync", DatabaseQuery),
        new("System.Data.Common.DbCommand", "ExecuteNonQuery", DatabaseQuery),
        new("System.Data.Common.DbCommand", "ExecuteNonQueryAsync", DatabaseQuery),
        new("System.Data.Common.DbCommand", "ExecuteScalar", DatabaseQuery),
        new("System.Data.Common.DbCommand", "ExecuteScalarAsync", DatabaseQuery),
        new("System.Data.IDbCommand", "ExecuteReader", DatabaseQuery),
        new("System.Data.IDbCommand", "ExecuteNonQuery", DatabaseQuery),
        new("System.Data.IDbCommand", "ExecuteScalar", DatabaseQuery),

        // Dapper — extension methods on IDbConnection. The Roslyn symbol for an
        // extension call has ContainingType = Dapper.SqlMapper.
        new("Dapper.SqlMapper", "Query", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryFirst", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryFirstAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryFirstOrDefault", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryFirstOrDefaultAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "QuerySingle", DatabaseQuery),
        new("Dapper.SqlMapper", "QuerySingleAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "QuerySingleOrDefault", DatabaseQuery),
        new("Dapper.SqlMapper", "QuerySingleOrDefaultAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryMultiple", DatabaseQuery),
        new("Dapper.SqlMapper", "QueryMultipleAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "Execute", DatabaseQuery),
        new("Dapper.SqlMapper", "ExecuteAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "ExecuteScalar", DatabaseQuery),
        new("Dapper.SqlMapper", "ExecuteScalarAsync", DatabaseQuery),
        new("Dapper.SqlMapper", "ExecuteReader", DatabaseQuery),
        new("Dapper.SqlMapper", "ExecuteReaderAsync", DatabaseQuery),

        // EF Core — SaveChanges is the write boundary. Read-side LINQ-to-SQL is
        // harder to detect symbolically (any IQueryable enumeration could be a
        // round trip), so we rely on EF projects routing through SaveChanges or
        // through a Dapper/repository surface for reads.
        new("Microsoft.EntityFrameworkCore.DbContext", "SaveChanges", DatabaseQuery),
        new("Microsoft.EntityFrameworkCore.DbContext", "SaveChangesAsync", DatabaseQuery),

        // EF6 / legacy.
        new("System.Data.Entity.DbContext", "SaveChanges", DatabaseQuery),
        new("System.Data.Entity.DbContext", "SaveChangesAsync", DatabaseQuery),
    };

    private static readonly IReadOnlyDictionary<(string, string), string> Lookup =
        All.ToDictionary(p => (p.TypeFqn, p.MethodName), p => p.Category);

    /// <summary>
    /// Returns the boundary category if the given (typeFqn, methodName) is a
    /// known I/O primitive. Caller is responsible for walking the type's base
    /// chain (e.g. SqlConnection : DbConnection) before calling this.
    /// </summary>
    public static string? Match(string typeFqn, string methodName)
        => Lookup.TryGetValue((typeFqn, methodName), out var cat) ? cat : null;
}
