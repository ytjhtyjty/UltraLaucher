namespace UltraLauncher.Core.Common;

/// <summary>Категории ошибок, понятные интерфейсу.</summary>
public enum ErrorCode
{
    Unknown,
    Network,
    Integrity,
    DiskFull,
    AccessDenied,
    InvalidPath,
    InvalidSettings,
    VersionNotFound,
    UnsupportedVersion,
    NotInstalled,
    JavaNotFound,
    JavaIncompatible,
    GameStartFailed,
    OperationInProgress,
    AuthNotConfigured,
    AuthCancelled,
    AuthFailed,
    AuthExpired,
    AuthNoXboxAccount,
    AuthChildAccount,
    AuthRegionBlocked,
    AuthAppNotApproved,
    AuthNoGame,
    AuthNoProfile,
    AccountRequired,
}

/// <summary>
/// Ошибка с текстом для пользователя и подсказкой, что делать.
/// Сообщения не должны содержать токены и другие секреты.
/// </summary>
public sealed class LauncherException : Exception
{
    public LauncherException(ErrorCode code, string userMessage, string? hint = null, Exception? inner = null)
        : base(userMessage, inner)
    {
        Code = code;
        UserMessage = userMessage;
        Hint = hint;
    }

    public ErrorCode Code { get; }
    public string UserMessage { get; }
    public string? Hint { get; }
}
