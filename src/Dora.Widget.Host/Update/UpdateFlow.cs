using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Dora.Widget.Host.Update;

/// <summary>Result of <see cref="UpdateFlow.InstallAsync"/>: what happened and what to tell the user.</summary>
internal sealed record UpdateOutcome(bool AppMustExit, string Message);

/// <summary>Check / download / verify / hand over to the replacement helper. UI-free; callers show the messages.</summary>
internal static class UpdateFlow
{
    public static string CurrentVersion { get; } =
        typeof(UpdateFlow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Text for the settings window, without the "+sha" suffix.</summary>
    public static string DisplayVersion
    {
        get
        {
            var v = CurrentVersion;
            var cut = v.IndexOf('+');
            return cut >= 0 ? v[..cut] : v;
        }
    }

    /// <summary>The newest release when it is newer than this build; null when up to date, offline or unknown.</summary>
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken ct)
    {
        var latest = await UpdateChecker.FetchLatestAsync(ct);
        return latest != null && UpdateChecker.IsNewer(latest.TagName, CurrentVersion) ? latest : null;
    }

    /// <summary>
    /// Replaces the running exe when it can (download, verify, helper); otherwise saves the zip to Downloads
    /// and shows it in Explorer for a manual install.
    /// </summary>
    public static async Task<UpdateOutcome> InstallAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            var sumsAsset = release.FindChecksums();
            var sums = sumsAsset != null ? await UpdateChecker.FetchTextAsync(sumsAsset.BrowserDownloadUrl, ct) : null;

            var exe = release.FindExe(SelfUpdater.ExeAssetName);
            if (exe != null && SelfUpdater.CanReplaceInPlace())
            {
                var path = await UpdateChecker.DownloadAssetAsync(exe, SelfUpdater.UpdateDirectory, progress, ct);
                if (sums == null || !UpdateChecker.VerifyChecksum(sums, exe.Name, path, requireEntry: true))
                {
                    TryDelete(path);
                    return new UpdateOutcome(false, "체크섬 검증에 실패해 업데이트를 중단했습니다.");
                }
                return SelfUpdater.LaunchHelper(path)
                    ? new UpdateOutcome(true, "업데이트를 적용하기 위해 다시 시작합니다.")
                    : new UpdateOutcome(false, "업데이트 도우미를 실행하지 못했습니다.");
            }

            var zip = release.FindZip();
            if (zip == null) return new UpdateOutcome(false, "이 릴리스에 내려받을 파일이 없습니다: " + release.HtmlUrl);
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var zipPath = await UpdateChecker.DownloadAssetAsync(zip, downloads, progress, ct);
            if (sums != null && !UpdateChecker.VerifyChecksum(sums, zip.Name, zipPath))
            {
                TryDelete(zipPath);
                return new UpdateOutcome(false, "체크섬 검증에 실패해 파일을 지웠습니다.");
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
            return new UpdateOutcome(false, "이 위치에서는 자동 교체를 할 수 없어 zip을 내려받았습니다. 압축을 풀어 덮어써 주세요:\n" + zipPath);
        }
        catch (OperationCanceledException) { return new UpdateOutcome(false, "업데이트를 취소했습니다."); }
        catch (Exception ex) { return new UpdateOutcome(false, "업데이트에 실패했습니다: " + ex.Message); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
