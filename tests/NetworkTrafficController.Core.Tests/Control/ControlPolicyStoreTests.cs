using Microsoft.Extensions.Logging.Abstractions;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class ControlPolicyStoreTests
{
    private static ControlPolicyStore Create()
    {
        var time = new FakeTimeProvider();
        return new ControlPolicyStore(new ProcessNameResolver(NullLogger.Instance, time), time);
    }

    private static readonly ControlPolicy Block = new() { Block = true };

    [Fact]
    public void SetPid_MakesConfiguredTarget_AndRetrievable()
    {
        var store = Create();
        store.SetPidPolicy(1234, Block);

        Assert.True(store.AnyConfigured);
        Assert.True(store.IsConfiguredTarget(1234));
        Assert.True(store.TryGetConfiguredPolicy(1234, out var p));
        Assert.True(p.Block);
        Assert.Contains(1234u, store.ConfiguredPids);
    }

    [Fact]
    public void RemovePid_ClearsTarget()
    {
        var store = Create();
        store.SetPidPolicy(1234, Block);
        store.RemovePid(1234);

        Assert.False(store.AnyConfigured);
        Assert.False(store.IsConfiguredTarget(1234));
        Assert.False(store.TryGetConfiguredPolicy(1234, out _));
    }

    [Fact]
    public void ExePolicy_IsListedAndRetrievable()
    {
        var store = Create();
        store.SetExePolicy("game.exe", Block);

        Assert.True(store.AnyConfigured);
        Assert.Contains("game.exe", store.ConfiguredExeNames);
        Assert.True(store.TryGetConfiguredPolicyByExe("game.exe", out var p));
        Assert.True(p.Block);
    }

    [Fact]
    public void GetController_ReturnsController_ForActivePolicy_NullWhenRemoved()
    {
        var store = Create();
        store.SetPidPolicy(1234, Block);
        Assert.NotNull(store.GetController(1234));

        store.RemovePid(1234);
        Assert.Null(store.GetController(1234));
    }

    [Fact]
    public void BlockOverride_ActivatesEvenInactiveBasePolicy_ForConfiguredTargetOnly()
    {
        var store = Create();
        store.SetPidPolicy(1234, new ControlPolicy()); // 非活性の基本ポリシー（対象登録のみ）
        Assert.Null(store.GetController(1234));         // 上乗せ前は実効制御なし

        store.SetTimedBlock(true);
        Assert.True(store.BlockOverride);
        var controller = store.GetController(1234);
        Assert.NotNull(controller);
        Assert.True(controller!.Policy.Block);          // Block が上乗せされる

        Assert.Null(store.GetController(9999));          // 未登録 PID には波及しない
    }

    // SPEC-0002: 上乗せの Block は、基本ポリシーの Block の向きに関係なく両方向
    [Fact]
    public void BlockOverride_IsAlwaysBothDirections()
    {
        var store = Create();
        store.SetPidPolicy(1234, new ControlPolicy { Block = true, BlockDirection = ControlDirection.Outbound });

        store.SetTimedBlock(true);
        var controller = store.GetController(1234);

        Assert.NotNull(controller);
        Assert.Equal(ControlDirection.Both, controller!.Policy.BlockDirection);
    }

    [Fact]
    public void ManualAndTimedBlock_AreIndependent()
    {
        var store = Create();
        store.SetPidPolicy(1234, new ControlPolicy());

        store.SetTimedBlock(true);
        Assert.True(store.ToggleManualBlock());   // manual ON
        Assert.True(store.BlockOverride);

        store.SetTimedBlock(false);               // 時限が切れても
        Assert.True(store.BlockOverride);         // 手動 ON は残る

        Assert.False(store.ToggleManualBlock());  // manual OFF
        Assert.False(store.BlockOverride);
    }

    [Fact]
    public void ClearAll_RemovesTargetsAndOverrides()
    {
        var store = Create();
        store.SetPidPolicy(1234, Block);
        store.SetExePolicy("game.exe", Block);
        store.SetTimedBlock(true);
        store.ToggleManualBlock();
        Assert.NotNull(store.GetController(1234));

        store.ClearAll();

        Assert.False(store.AnyConfigured);
        Assert.False(store.BlockOverride);
        Assert.Null(store.GetController(1234));
        Assert.False(store.TryGetExistingController(1234, out _));
    }

    [Fact]
    public void TryGetExistingController_DoesNotCreate()
    {
        var store = Create();
        store.SetPidPolicy(1234, Block);
        Assert.False(store.TryGetExistingController(1234, out _)); // まだ判定していない
        var created = store.GetController(1234);
        Assert.True(store.TryGetExistingController(1234, out var existing));
        Assert.Same(created, existing);
    }

    [Fact]
    public void Validate_IsEnforced_OnSet()
    {
        var store = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetPidPolicy(1, new ControlPolicy { DropProbability = 5 }));
    }
}
