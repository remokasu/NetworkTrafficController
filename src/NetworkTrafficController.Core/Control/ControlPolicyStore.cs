using System.Collections.Concurrent;
using NetworkTrafficController.Core.Monitoring;

namespace NetworkTrafficController.Core.Control;

/// <summary>
/// 制御対象のポリシーを保持する。PID 指定と exe 名指定（ファイル名、大文字小文字無視。SPEC 決定事項）の両方に対応。
/// PID → <see cref="TargetController"/> を遅延生成してシェーパ状態を保持する。exe 名指定は同名の全プロセスで
/// 1 つのポリシーを共有する（バケットは PID ごと。SPEC は「その exe の合計を制限」だが、フェーズ 3 では
/// PID ごとのバケットで近似し、共有バケット化はフェーズ 4 の課題とする）。
/// 時限 Block / ホットキーは、設定済みの各ポリシーに Block を上乗せする <see cref="SetTimedBlock"/> で表す。
/// </summary>
/// <remarks>
/// PID 指定は、指定時点のプロセス開始時刻を控えて照合する。対象が終了して同じ PID が別プロセスに再利用されても
/// 制御がかからないようにするため（security review）。開始時刻は <see cref="ProcessNameResolver"/> のキャッシュ（TTL）
/// 経由で取るので、再利用が TTL 以内に起きた場合は検出が遅れる。
/// </remarks>
public sealed class ControlPolicyStore
{
    private readonly record struct PidTarget(ControlPolicy Policy, long CreationTime);

    private readonly ConcurrentDictionary<uint, PidTarget> _byPid = new();
    private readonly ConcurrentDictionary<string, ControlPolicy> _byExeName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, TargetController> _controllers = new();
    private readonly ProcessNameResolver _resolver;
    private readonly TimeProvider _time;

    // Block の上乗せは 2 系統を独立に持ち、実効 = manual OR timed とする。こうすると時限 Block の自動復帰が
    // ホットキーの手動操作を打ち消さない（レビュー指摘）。手動 = ホットキー、時限 = 時限 Block。
    private volatile bool _manualBlock;
    private volatile bool _timedBlock;

    /// <summary>名前解決器と時間源を指定して生成する。</summary>
    public ControlPolicyStore(ProcessNameResolver resolver, TimeProvider time)
    {
        _resolver = resolver;
        _time = time;
    }

    /// <summary>PID または exe 名の対象が 1 つ以上設定されているか（制御ハンドルを開くかの判断に使う）。</summary>
    public bool AnyConfigured => !_byPid.IsEmpty || !_byExeName.IsEmpty;

    /// <summary>いずれかの Block 上乗せ（手動 or 時限）が有効か。</summary>
    public bool BlockOverride => _manualBlock || _timedBlock;

    /// <summary>時限 Block による上乗せを設定する（<see cref="TimedBlockController"/> から呼ぶ）。手動側は変えない。</summary>
    public void SetTimedBlock(bool blocked) => _timedBlock = blocked;

    /// <summary>手動（ホットキー）の Block 上乗せを反転する。時限側は変えない。</summary>
    /// <returns>反転後の手動状態。</returns>
    public bool ToggleManualBlock()
    {
        _manualBlock = !_manualBlock;
        return _manualBlock;
    }

    /// <summary>PID を対象に設定する。その時点のプロセス開始時刻を控える（取れなければ PID のみで照合）。</summary>
    public void SetPidPolicy(uint pid, ControlPolicy policy)
    {
        policy.Validate();
        long creation = _resolver.Resolve(pid).CreationTime;
        _byPid[pid] = new PidTarget(policy, creation);
    }

    /// <summary>exe 名（ファイル名）を対象に設定する。</summary>
    public void SetExePolicy(string exeName, ControlPolicy policy)
    {
        policy.Validate();
        _byExeName[exeName] = policy;
    }

    /// <summary>PID の対象設定を消す（コントローラも捨てる）。</summary>
    public void RemovePid(uint pid)
    {
        _byPid.TryRemove(pid, out _);
        _controllers.TryRemove(pid, out _);
    }

    /// <summary>すべての対象設定と Block の上乗せ（手動・時限）を消す（「すべて解除」用）。保存済みの設定ファイルには触れない。</summary>
    public void ClearAll()
    {
        _byPid.Clear();
        _byExeName.Clear();
        _controllers.Clear();
        _manualBlock = false;
        _timedBlock = false;
    }

    /// <summary>
    /// 既に作られているコントローラを返す（新しくは作らない）。表示用に対象ごとの破棄数などを読むのに使う。
    /// 制御がまだ 1 パケットも判定していない対象では false。
    /// </summary>
    public bool TryGetExistingController(uint pid, out TargetController controller) =>
        _controllers.TryGetValue(pid, out controller!);

    /// <summary>設定済みの exe 名対象を列挙する（設定の保存用）。</summary>
    public IReadOnlyCollection<string> ConfiguredExeNames => (IReadOnlyCollection<string>)_byExeName.Keys;

    /// <summary>設定済みの PID 対象を列挙する（終了したプロセスの掃除用）。</summary>
    public IReadOnlyCollection<uint> ConfiguredPids => _byPid.Keys.ToArray();

    /// <summary>exe 名に設定されたポリシーを返す。</summary>
    public bool TryGetConfiguredPolicyByExe(string exeName, out ControlPolicy policy) =>
        _byExeName.TryGetValue(exeName, out policy!);

    /// <summary>PID または一致する exe 名に設定された基本ポリシーを返す（表示用。Block 上乗せは含めない）。</summary>
    public bool TryGetConfiguredPolicy(uint pid, out ControlPolicy policy) => TryResolveBasePolicy(pid, out policy);

    /// <summary>PID が制御対象として設定されているか（フィルタ生成用。Block 上乗せの有無に関わらず判定する）。</summary>
    public bool IsConfiguredTarget(uint pid) => TryResolveBasePolicy(pid, out _);

    /// <summary>PID → 基本ポリシー（Block 上乗せなし）を解決する。PID 指定（開始時刻一致）を優先し、無ければ exe 名一致。</summary>
    private bool TryResolveBasePolicy(uint pid, out ControlPolicy policy)
    {
        if (_byPid.TryGetValue(pid, out var target) && MatchesCreation(pid, target))
        {
            policy = target.Policy;
            return true;
        }

        if (!_byExeName.IsEmpty && TryGetExePolicy(pid, out policy))
        {
            return true;
        }

        policy = null!;
        return false;
    }

    /// <summary>
    /// PID に対する <see cref="TargetController"/> を返す。実効的な制御が無ければ null（残っていたコントローラも捨てる）。
    /// 実効ポリシーが変わっていれば差し替える（シェーパ状態は保持）。
    /// </summary>
    public TargetController? GetController(uint pid)
    {
        var policy = ResolveEffectivePolicy(pid);
        if (policy is null)
        {
            _controllers.TryRemove(pid, out _);
            return null;
        }

        var controller = _controllers.GetOrAdd(pid, _ => new TargetController(policy, _time));
        if (controller.Policy != policy)
        {
            controller.SetPolicy(policy);
        }

        return controller;
    }

    private ControlPolicy? ResolveEffectivePolicy(uint pid)
    {
        if (!TryResolveBasePolicy(pid, out var basePolicy))
        {
            return null;
        }

        // 上乗せの Block（ホットキー / 時限）は常に両方向（SPEC-0002）
        var effective = BlockOverride ? basePolicy with { Block = true, BlockDirection = ControlDirection.Both } : basePolicy;
        return effective.IsActive ? effective : null;
    }

    private bool MatchesCreation(uint pid, PidTarget target) =>
        target.CreationTime == 0 || _resolver.Resolve(pid).CreationTime == target.CreationTime;

    private bool TryGetExePolicy(uint pid, out ControlPolicy policy)
    {
        var info = _resolver.Resolve(pid);
        if (info.Resolved && _byExeName.TryGetValue(info.Name, out var found))
        {
            policy = found;
            return true;
        }

        policy = null!;
        return false;
    }
}
