namespace Recorrencia.Api;

public sealed class AuthOptions
{
    public int IdleTimeoutSeconds { get; set; } = 43200;
    public int AbsoluteTimeoutSeconds { get; set; } = 604800;
    public int InviteTtlSeconds { get; set; } = 259200;
    public int ResetTtlSeconds { get; set; } = 3600;
    public int MaxFailuresPerWindow { get; set; } = 10;
    public int FailureWindowSeconds { get; set; } = 900;
}

public sealed class Argon2Options
{
    public int MemoryKb { get; set; } = 19456;
    public int Iterations { get; set; } = 2;
    public int Parallelism { get; set; } = 1;
}

public sealed class WebOptions
{
    public string Scheme { get; set; } = "https";
    public string RootDomain { get; set; } = "";
}

public sealed class InternalOptions
{
    public string Key { get; set; } = "";
}

public sealed class EmailOptions
{
    public string OutboxDir { get; set; } = "";
}
