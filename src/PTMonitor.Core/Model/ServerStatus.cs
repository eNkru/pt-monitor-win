namespace PTMonitor.Core.Model;

public enum FailureKind
{
    Unreachable,
    Auth,
    Banned,
    ProtocolViolation,
    ServerError,
}

/// <summary>Cause-specific server failure, so the panel can show an actionable hint.</summary>
public sealed record ServerFailure(FailureKind Kind, string Message)
{
    public string KindLabel => Kind switch
    {
        FailureKind.Unreachable => "unreachable",
        FailureKind.Auth => "auth failed",
        FailureKind.Banned => "banned",
        FailureKind.ProtocolViolation => "unexpected response",
        FailureKind.ServerError => "server error",
        _ => "error",
    };

    public static ServerFailure Unreachable(string message) => new(FailureKind.Unreachable, message);
    public static ServerFailure Auth(string message) => new(FailureKind.Auth, message);
    public static ServerFailure Banned(string message) => new(FailureKind.Banned, message);
    public static ServerFailure ProtocolViolation(string message) => new(FailureKind.ProtocolViolation, message);
    public static ServerFailure ServerError(string message) => new(FailureKind.ServerError, message);
}

/// <summary>Thrown by clients so the poller can place the cause straight into status.</summary>
public sealed class ServerFailureException : Exception
{
    public ServerFailure Failure { get; }

    public ServerFailureException(ServerFailure failure) : base(failure.Message)
    {
        Failure = failure;
    }

    public ServerFailureException(FailureKind kind, string message) : this(new ServerFailure(kind, message))
    {
    }
}

/// <summary>Live state of one configured server.</summary>
public abstract record ServerStatus
{
    private ServerStatus()
    {
    }

    public sealed record Disabled : ServerStatus
    {
        public static readonly Disabled Instance = new();
    }

    public sealed record Connecting : ServerStatus
    {
        public static readonly Connecting Instance = new();
    }

    public sealed record Online(TransferSnapshot Value) : ServerStatus;

    public sealed record Failed(ServerFailure Error) : ServerStatus;

    public TransferSnapshot? Snapshot => this is Online online ? online.Value : null;

    public ServerFailure? Failure => this is Failed failed ? failed.Error : null;

    public static ServerStatus FromFailure(ServerFailure failure) => new Failed(failure);
}
