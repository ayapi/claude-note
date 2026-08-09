namespace ClaudeNote;

/// <summary>ユーザーに通知バルーンでそのまま見せてよいエラー。</summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
}

/// <summary>--resume に失敗した場合。呼び出し側で新規セッションにフォールバックする。</summary>
public sealed class SessionResumeException : UserFacingException
{
    public string SessionId { get; }

    public SessionResumeException(string sessionId, string message) : base(message)
    {
        SessionId = sessionId;
    }
}
