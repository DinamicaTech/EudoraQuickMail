using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A verified installed or portable update is available for the in-app update dialog.
    /// </summary>
    bool SelfUpdatePending { get; }

    Task PrepareUpdateAsync(IProgress<string>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Arms the prepared updater after work has drained. The caller must immediately perform
    /// a normal application shutdown on success. No updater forcibly terminates this process.
    /// </summary>
    Task<bool> RestartToUpdateAsync(CancellationToken cancellationToken = default);
}
