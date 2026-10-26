using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace NetworkTrafficController.Core.Native;

/// <summary>起動時の配置フォルダ検査の結果（警告のみ。起動は止めない）。</summary>
/// <param name="Warnings">検出した問題の説明。空なら問題なし。</param>
public sealed record InstallCheckResult(IReadOnlyList<string> Warnings)
{
    /// <summary>問題がなかったか。</summary>
    public bool IsClean => Warnings.Count == 0;
}

/// <summary>
/// 管理者権限で exe フォルダのファイルを読み込む前の多層防御（security review MEDIUM-1）:
/// (1) WinDivert.dll / .sys の SHA-256 を配布時の既知値と照合、(2) exe フォルダが一般ユーザーから書き込み可能かを検査、
/// (3) ログフォルダなどが reparse point（ジャンクション / シンボリックリンク）でないかを検査。
/// いずれも警告を返すだけで、判断は呼び出し側（CLI は表示、GUI は警告表示）に委ねる。
/// </summary>
public static class InstallDirectoryGuard
{
    /// <summary>
    /// 同梱する WinDivert 2.2.2-A x64 バイナリの SHA-256。変更したら scripts\publish.ps1 の $known も同時に更新すること。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["WinDivert.dll"] = "c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2",
        ["WinDivert64.sys"] = "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2",
    };

    private static readonly SecurityIdentifier[] TrustedWriters =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.CreatorOwnerSid, null),
    ];

    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>exe フォルダ配下のネイティブ配布物とフォルダの権限を検査する。</summary>
    public static InstallCheckResult Check(ILogger logger)
    {
        var warnings = new List<string>();
        string baseDir = AppContext.BaseDirectory;

        foreach (var (file, expected) in KnownHashes)
        {
            string path = Path.Combine(baseDir, file);
            if (!File.Exists(path))
            {
                continue; // 存在しない場合は EnsureNativeFilesPresent が扱う
            }

            string actual = HashFile(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"The SHA-256 of {file} does not match the bundled version (actual: {actual[..16]}…). It has been replaced or is a different version");
            }
        }

        if (IsWritableByNonAdmins(baseDir, logger))
        {
            warnings.Add($"Non-administrators have write access to the install folder {baseDir}. Files could be replaced before running as administrator, so installing under Program Files or similar is recommended");
        }

        foreach (var w in warnings)
        {
            logger.LogWarning("InstallDirectoryGuard: {Warning}", w);
        }

        return new InstallCheckResult(warnings);
    }

    /// <summary>指定パス（またはその既存の祖先）が reparse point なら例外にする。管理者権限での書き込み前に呼ぶ。</summary>
    /// <exception cref="IOException">reparse point を検出した場合。</exception>
    public static void EnsureNotReparsePoint(string path)
    {
        string full = Path.GetFullPath(path);
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"{current} is a junction or symbolic link. Refusing it as a write target with administrator privileges");
            }
        }
    }

    private static string HashFile(string path)
    {
        // 照合してから読み込むまでの差し替え（TOCTOU）を抑えるため、共有書き込みを拒否して開いたまま計算する
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool IsWritableByNonAdmins(string directory, ILogger logger)
    {
        try
        {
            var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteRights) == 0)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.Value == TrustedInstallerSid || TrustedWriters.Any(t => t.Equals(sid)))
                {
                    continue;
                }

                logger.LogDebug("InstallDirectoryGuard: write access for {Sid} ({Rights}) on {Dir}", sid.Value, rule.FileSystemRights, directory);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "InstallDirectoryGuard: ACL check failed for {Dir}", directory);
            return false;
        }
    }
}
