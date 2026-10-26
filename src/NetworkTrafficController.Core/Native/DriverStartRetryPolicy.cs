using NetworkTrafficController.Core.Exceptions;

namespace NetworkTrafficController.Core.Native;

/// <summary>
/// WinDivert を開くのに一時的な理由で失敗したとき、待ってからやり直すかを決める。
/// ドライバが停止処理の途中（433）や、古いサービス登録の片付け途中（1058）のときは、数秒から数十秒で開けるようになる。
/// 1 回の失敗で諦めると、利用者は理由の分からないエラーを見て手で起動し直すことになる（2026-09-30 の不具合）。
/// </summary>
/// <param name="MaxAttempts">最初の 1 回を含めた試行回数の上限。</param>
/// <param name="Interval">やり直すまでの待ち時間。</param>
public sealed record DriverStartRetryPolicy(int MaxAttempts, TimeSpan Interval)
{
    /// <summary>既定: 2 秒おきに最大 10 回（約 20 秒）。</summary>
    public static DriverStartRetryPolicy Default { get; } = new(10, TimeSpan.FromSeconds(2));

    /// <summary><paramref name="attemptsSoFar"/> 回目の試行が <paramref name="ex"/> で失敗したとき、もう一度試すか。</summary>
    public bool ShouldRetry(WinDivertException ex, int attemptsSoFar) =>
        WinDivertException.IsTransient(ex.Failure) && attemptsSoFar < MaxAttempts;
}
